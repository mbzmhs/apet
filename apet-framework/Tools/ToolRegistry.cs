using System.Text.Encodings.Web;
using System.Text.Json;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools;

/// <summary>工具注册中心:注册工具、导出定义、按名称分发执行。所有执行必须通过安全鉴权。</summary>
public sealed class ToolRegistry
{
    private static readonly JsonSerializerOptions s_errorJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // MCP 断连/重连在后台线程 Unregister,运行循环同时 TryGetValue/枚举:普通 Dictionary 非线程安全,
    // 会随机抛 "Collection was modified" 甚至内部损坏——注册表必须并发容器
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IApetTool> _tools = new(StringComparer.Ordinal);
    private readonly ILog _log;
    private readonly ISecurityChecker _security;
    private Func<IEnumerable<IApetTool>>? _dynamicTools;

    public ToolRegistry(ILog? log = null, ISecurityChecker? securityChecker = null)
    {
        _log = log ?? Log.Global;
        _security = securityChecker ?? SecurityManager.Instance;
    }

    /// <summary>
    /// 注册动态工具提供器(如 Agent 把启用了 IsTool 的插件以适配器形式接入)。
    /// 动态工具与注册工具同样走安全鉴权与执行管线;同名时以 Register 注册的工具优先。
    /// </summary>
    public void SetDynamicToolsProvider(Func<IEnumerable<IApetTool>>? provider) =>
        _dynamicTools = provider;

    private IEnumerable<IApetTool> DynamicTools() =>
        _dynamicTools?.Invoke() ?? Enumerable.Empty<IApetTool>();

    private IEnumerable<IApetTool> DynamicEnabledTools() => DynamicTools().Where(t => t.Enabled);

    /// <summary>枚举全部注册(非动态)的工具实例,宿主可用于设置界面(读取/切换 Enabled)。</summary>
    public IReadOnlyList<IApetTool> GetRegisteredTools() => _tools.Values.ToList();

    /// <summary>
    /// 枚举全部工具实例(注册工具 + 全部插件动态工具,含插件整体禁用者,同名以注册工具优先)。
    /// 设置界面用它获得插件工具(如 skill)的清单与启用状态,插件关闭时仍可经工具页重新开启。
    /// </summary>
    public IReadOnlyList<IApetTool> GetAllTools()
    {
        var list = _tools.Values.ToList();
        var names = new HashSet<string>(_tools.Keys, StringComparer.Ordinal);
        foreach (var tool in DynamicTools())
            if (names.Add(tool.Definition.Name))
                list.Add(tool);
        return list;
    }

    /// <summary>已注册工具数量。</summary>
    public int Count => _tools.Count;

    public void Register(IApetTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_tools.TryAdd(tool.Definition.Name, tool))
            throw new InvalidOperationException($"Tool {tool.Definition.Name} is already registered.");
    }

    /// <summary>注销工具(供连接型动态工具在断开/重连时回收);返回该名称是否存在过。</summary>
    public bool Unregister(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _tools.TryRemove(name, out _);
    }

    /// <summary>导出全部已启用工具的定义(注册工具 + 动态工具,同名以注册工具优先),提供给 LLM。</summary>
    public List<ToolDefinition> GetDefinitions()
    {
        var definitions = new List<ToolDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in _tools.Values.Where(t => t.Enabled))
        {
            names.Add(tool.Definition.Name);
            definitions.Add(tool.Definition);
        }
        foreach (var tool in DynamicEnabledTools())
        {
            if (names.Add(tool.Definition.Name))
                definitions.Add(tool.Definition);
        }
        return definitions;
    }

    /// <summary>
    /// 按名称执行工具(注册工具或动态工具),流程:存在且启用 → 解析影响范围 → 安全鉴权(可由宿主回调改判) → 执行。
    /// 返回富结果 ToolResult(文本 + 可选图片附件;实现了 IApetToolMultimodal 的工具走多模态路径)。
    /// 工具不存在、已禁用、鉴权被拒或执行异常都不会抛出,
    /// 而是返回形如 {"error":"...","code":"..."} 的 JSON 文本结果,交由 LLM 决定后续行为。
    /// securityPolicy 为空时使用默认安全策略(工作目录读写,其他目录只读,白名单放行)。
    /// authorizationOverride:宿主改判回调(收到的第二个参数是安全策略的原始判定),可采纳或改判。
    /// output:工具执行中的实时输出出口(流名 + 文本增量),工具经 ToolExecutionScope.Output 取用。
    /// 唯一会上抛的异常是"整轮取消"(cancellationToken 已触发):取消是运行语义,不是工具错误。
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(
        string name,
        string argumentsJson,
        AgentSecurityPolicy? securityPolicy = null,
        Func<ToolEffect, AuthorizationResult, Task<AuthorizationResult>>? authorizationOverride = null,
        CancellationToken cancellationToken = default,
        Func<int>? contextUsage = null,
        Action<string, string>? output = null)
    {
        IApetTool? tool;
        if (_tools.TryGetValue(name, out var registered))
        {
            tool = registered.Enabled ? registered : null; // 已注册但禁用:不回退到动态工具,直接报错
        }
        else
        {
            tool = DynamicEnabledTools().FirstOrDefault(t => t.Definition.Name == name);
        }

        if (tool is null)
        {
            _log.Error($"Tool execution failed: {name} not registered or disabled");
            return ToolResult.FromText(ErrorJson($"Tool {name} is not registered or is disabled.", "tool_unavailable"));
        }

        var policy = securityPolicy ?? new AgentSecurityPolicy();

        // 会话工作目录 + 会话标识 + 工具白名单 + 实时上下文用量注入执行环境:工作目录供相对路径/终端 CWD,
        // 会话标识供按会话隔离状态的插件工具(TODO/后台任务),白名单供特定来源的对话轮在调用时收窄可用工具,
        // 上下文用量供按"剩余上下文"计算预算的工具
        using (ToolExecutionScope.Enter(policy.WorkingDirectory, policy.SessionId, policy.AllowedTools, contextUsage, output))
        {
            // 来源白名单(如 B站直播触发的轮次)在调用时统一拦截:不改请求中的工具定义(保证提示词缓存),
            // 模型调用不在白名单内的工具时返回错误结果,交由模型改用其他方式或如实说明
            var allowed = ToolExecutionScope.AllowedTools;
            if (allowed is not null && !allowed.Contains(name))
            {
                _log.Info($"Tool {name} blocked by the round's tool whitelist");
                return ToolResult.FromText(ErrorJson(
                    $"Tool {name} is not allowed in this conversation round.", "tool_not_allowed"));
            }

            ToolEffect effect;
            try
            {
                effect = tool.AnalyzeEffect(argumentsJson);
            }
            catch (Exception ex)
            {
                // 意图解析不在下方执行 try 内,异常会直落 RunAsync 打死整轮(而非回工具错误给模型自愈):
                // 任何解析异常(如模型漏写 command 键)按"效果未知"处理,交安全策略/宿主回调 fail-closed 裁决
                _log.Error($"Tool {name} effect analysis failed", ex);
                effect = new ToolEffect { Access = FileAccessKind.Unknown };
            }
            var decision = _security.Check(policy, effect, name); // 工具名用于白名单按权限类别匹配

            // 策略未放行 → 交给宿主回调决定;回调默认实现为拒绝
            if (!decision.Allowed && authorizationOverride is not null)
            {
                decision = await authorizationOverride(effect, decision);
                _log.Info($"Tool {name} host authorization callback user={policy.UserId} allowed={decision.Allowed} reason={decision.Reason}");
            }

            if (!decision.Allowed)
            {
                _log.Error($"Tool {name} denied by security policy user={policy.UserId}: {decision.Reason}");
                return ToolResult.FromText(ErrorJson($"Tool {name} is denied by the security policy: {decision.Reason}", "authorization_denied"));
            }

            try
            {
                // 多模态工具(如截图)返回富结果;其余工具只产出文本
                if (tool is IApetToolMultimodal multimodal)
                    return await multimodal.ExecuteRichAsync(argumentsJson, cancellationToken);
                return ToolResult.FromText(await tool.ExecuteAsync(argumentsJson, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 整轮取消:原样上抛,Agent 据此收口为 Cancelled
            }
            catch (OperationCanceledException ex)
            {
                // 整轮并未取消而工具自身抛出取消(多为工具内部超时,如 web_fetch 的请求超时):
                // 这是工具层面的失败,必须按工具错误返回,不得让整轮对话随之"被中断"
                _log.Error($"Tool {name} cancelled without run cancellation (internal timeout or self-cancel)", ex);
                return ToolResult.FromText(ErrorJson(
                    $"Tool {name} was cancelled before finishing (its own timeout or internal cancellation): {ex.Message}",
                    "tool_cancelled"));
            }
            catch (Exception ex)
            {
                _log.Error($"Tool {name} execution error", ex);
                return ToolResult.FromText(ErrorJson($"Tool {name} execution failed: {ex.Message}", "execution_error"));
            }
        }
    }

    private static string ErrorJson(string message, string code) =>
        JsonSerializer.Serialize(new { error = message, code }, s_errorJson);
}
