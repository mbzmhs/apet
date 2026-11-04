using APet.Framework.Agent;
using APet.Framework.LLM;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;
using APet.Framework.Tools.BuiltIn;
using APet.Plugins.ContextCompression;
using APet.Plugins.EnvContext;

namespace APet.Plugins.SubAgent;

/// <summary>
/// 子代理插件:让主 Agent 把自包含的子任务委派给预先配置的子代理。
/// 子代理 = 独立 Agent(新建 ToolRegistry + 勾选的内置基础工具子集,挂环境注入与按宿主注入的上下文压缩插件)+ 独立会话,
/// 一轮对话结束后其最后助手正文作为 sub_wait 工具结果返回给主 Agent。
/// 宿主注入 LLM 连接工厂 / 工具配置 / 事件出口 / 鉴权处理 / 策略解析;插件自身不依赖 UI 层。
/// </summary>
public sealed class SubAgentPlugin : AgentPluginBase
{
    public override string Name => "SubAgent";

    /// <summary>子代理可用工具集(设置页勾选项与 sub 工具执行共用):内置基础工具。</summary>
    public static readonly string[] AllowedToolNames =
        ["read_file", "write_file", "edit_file", "apply_patch", "bash", "web_fetch"];

    /// <summary>子代理定义(设置页建立):名称 + 系统提示词 + 工具集(内置工具名) + 供应商/模型/思考等级(空 = 继承主 Agent 会话) + 最大工具轮数(0 = 不限)。</summary>
    public sealed record Definition(string Name, string SystemPrompt, IReadOnlyList<string> Tools,
        string Provider = "", string Model = "", string ThinkingEffort = "", string WorkingDirectoryAccess = "ReadOnly",
        int MaxToolIterations = 0);

    private readonly object _gate = new();
    private IReadOnlyList<Definition> _definitions = Array.Empty<Definition>();

    /// <summary>子代理运行管理器(单 Agent 一份,内存态)。</summary>
    public SubAgentRunManager Manager { get; } = new();

    // ───────── 宿主注入 ─────────

    /// <summary>子代理 LLM 连接工厂(宿主注入;参数 = 主会话 Id, 子代理供应商, 子代理模型, 子代理思考等级;
    /// 供应商为空 = 继承主会话供应商,模型/等级为空 = 继承主会话所选,解析不到回退 Agent 默认连接)。</summary>
    public Func<string?, string?, string?, string?, ILlmClient?>? LlmClientFactory { get; set; }

    /// <summary>子代理上下文压缩插件工厂(宿主注入;参数 = 发起 sub_run 的主会话 Id, 子代理供应商, 子代理模型,
    /// 与 LlmClientFactory 同一解析规则)。返回按主 Agent 压缩设定配置好的插件实例(阈值/策略/保留窗口/摘要上限/模板
    /// 随 Agent 设定,ContextLimit 由宿主按子代理生效的供应商/模型解析);返回 null = 不压缩(自动压缩关闭等)。
    /// 每次运行新建实例;未注入时子代理不挂压缩。</summary>
    public Func<string?, string?, string?, ContextCompressionPlugin?>? CompressionPluginFactory { get; set; }

    /// <summary>子代理内置工具批量配置(终端 shell / web_fetch 代理,与主 Agent 同源)。</summary>
    public Action<BuiltInToolOptions>? ToolConfig { get; set; }

    /// <summary>事件出口 (runId, 事件类型, 载荷) → 宿主转发 Web 事件总线(模态框/侧边栏消费)。</summary>
    public Action<string, string, object?>? EventSink { get; set; }

    /// <summary>子代理工具鉴权处理:主会话未放行时由宿主出示卡片(主聊天窗)并等待用户裁决。</summary>
    public Func<SubAgentAuthContext, CancellationToken, Task<AuthorizationResult>>? AuthHandler { get; set; }

    /// <summary>按主会话 Id 解析其运行期安全策略(工作目录/权限/信任目录随 sub_run 继承)。</summary>
    public Func<string?, AgentSecurityPolicy?>? PolicyResolver { get; set; }

    /// <summary>工具显示名解析(子代理工具的卡片标题与主聊天一致)。</summary>
    public Func<string, string>? DisplayNames { get; set; }

    /// <summary>
    /// 工具帧格式化挂钩(宿主注入 UI 层展示格式化,使子代理模态框的工具行与主聊天同一观感:
    /// 摘要/详情/文件 diff/行数统计/成败判定)。参数 = (工具名, 显示名, 参数 JSON, 结果文本(null = 启动帧), 原始 isError, 会话工作目录);
    /// 返回 null 或抛异常时回退原始 args/result 帧。
    /// </summary>
    public Func<string, string, string, string?, bool, string?, object?>? ToolFrameFormatter { get; set; }

    /// <summary>是否还有任一 sub_* 工具处于启用状态(宿主注入);全部禁用时不注入使用指引。</summary>
    public Func<bool>? ToolsAvailable { get; set; }

    /// <summary>
    /// 用量记账出口(宿主注入):参数 = (发起主会话 Id, 子代理定义供应商, 定义模型, 本次请求用量)。
    /// 宿主按与 LlmClientFactory 同一解析规则求出子代理实际生效的供应商/模型(定义留空 = 继承主会话所选),
    /// 按该模型计费因子把 tokens+费用原子累计进发起会话的累计统计(占用条/TPS 等主对话实时指示不受影响)。
    /// 子代理内部自动压缩的用量也流经此处(压缩插件向本运行回调链 ReportUsageAsync)。未注入 = 不记账。
    /// </summary>
    public Action<string?, string, string, APet.Framework.LLM.LlmUsage>? UsageAccounting { get; set; }

    /// <summary>当前子代理定义清单(不可变快照)。</summary>
    public IReadOnlyList<Definition> Definitions
    {
        get { lock (_gate) return _definitions; }
    }

    /// <summary>宿主推送子代理定义清单(构造与设置页保存共用)。</summary>
    public void SetDefinitions(IEnumerable<Definition> definitions)
    {
        var list = definitions
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new Definition(
                d.Name.Trim(),
                d.SystemPrompt ?? "",
                (d.Tools ?? []).Where(t => AllowedToolNames.Contains(t, StringComparer.Ordinal)).ToList(),
                d.Provider?.Trim() ?? "",
                d.Model?.Trim() ?? "",
                d.ThinkingEffort?.Trim() ?? "",
                string.IsNullOrWhiteSpace(d.WorkingDirectoryAccess) ? "ReadOnly" : d.WorkingDirectoryAccess.Trim(),
                Math.Clamp(d.MaxToolIterations, 0, 1000)))
            .ToList();
        lock (_gate)
            _definitions = list;
    }

    /// <summary>按名称查找定义(不分大小写)。</summary>
    public Definition? FindDefinition(string name) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public override void Initialize() { }

    public override void Dispose()
    {
        // 宿主关闭/Agent 删除:终止仍在运行的子代理
        foreach (var run in Manager.Snapshot().Where(r => r.Status == SubAgentStatus.Running).ToList())
            run.Cancel();
    }

    /// <summary>
    /// 启动一次子代理运行(后台执行,立即返回运行实体);已达并发上限时返回 null
    /// (上限判定在 Manager.Start 的登记锁内原子完成,预检查之外的并发突破由锁内复查兜住)。
    /// 运行与主轮取消令牌联动:用户停止主对话时子代理一并终止;主轮正常结束则子代理存活。
    /// </summary>
    public SubAgentRun? Launch(Definition def, string prompt, string title, AgentSecurityPolicy? policy, CancellationToken mainToken)
    {
        var client = LlmClientFactory?.Invoke(ToolExecutionScope.SessionId, def.Provider, def.Model, def.ThinkingEffort)
            ?? throw new InvalidOperationException("SubAgent plugin is not wired to a host (LlmClientFactory is null).");
        if (client is null)
            throw new InvalidOperationException("主会话未选择供应商/模型,无法启动子代理。");
        // 运行与发起它的主会话绑定(侧边栏按会话过滤;其他对话看不到本会话之外的子代理)
        var run = Manager.Start(def.Name, prompt, title, ToolExecutionScope.SessionId, EventSink);
        if (run is null)
            return null;
        _ = LaunchAsync(run, def, prompt, policy, mainToken, client);
        return run;
    }

    private async Task LaunchAsync(SubAgentRun run, Definition def, string prompt,
        AgentSecurityPolicy? policy, CancellationToken mainToken, ILlmClient client)
    {
        // 联动主轮取消令牌:主对话被用户停止 → 子代理直接终止(Register 失败按不可取消处理)
        CancellationTokenRegistration? linked = null;
        if (mainToken.CanBeCanceled)
        {
            try { linked = mainToken.Register(() => run.Cancel()); }
            catch (ObjectDisposedException) { /* 主轮恰好在收尾:不联动 */ }
        }
        AgentSession? session = null;
        try
        {
            var tools = new ToolRegistry().RegisterBuiltIns(ToolConfig);
            foreach (var tool in tools.GetRegisteredTools())
                if (!def.Tools.Contains(tool.Definition.Name, StringComparer.Ordinal))
                    tool.Enabled = false; // 未勾选的工具不暴露给子代理(apply_patch 等同样禁用)

            // 运行路径只经 session.LlmClient 发消息(模型/地址在连接实例里),AgentOptions.Llm 仅 CreateSession 用
            // 安全策略克隆自主会话(工作目录/白名单随 sub_run 继承),
            // 但工作目录权限改用子代理自身设定(默认只读)——绝不回写主会话的策略对象
            var sec = policy is null
                ? new AgentSecurityPolicy()
                : new AgentSecurityPolicy
                {
                    UserId = policy.UserId,
                    SessionId = policy.SessionId,
                    AllowedTools = policy.AllowedTools,
                    WorkingDirectory = policy.WorkingDirectory,
                    WorkingDirectoryAccess = policy.WorkingDirectoryAccess,
                };
            if (policy is not null)
                foreach (var rule in policy.WhitelistedDirectories)
                    sec.WhitelistedDirectories.Add(rule);
            sec.WorkingDirectoryAccess = def.WorkingDirectoryAccess switch
            {
                "ReadWrite" => DirectoryAccess.ReadWrite,
                "FullAccess" => DirectoryAccess.FullAccess,
                "Ask" => DirectoryAccess.Ask,
                _ => DirectoryAccess.ReadOnly,
            };

            var agent = new Agent(new AgentOptions
            {
                Llm = new LlmClientOptions(),
                SystemPrompt = def.SystemPrompt,
                MaxToolIterations = Math.Clamp(def.MaxToolIterations, 0, 1000),
                Security = sec,
            }, tools);
            // 环境注入:与主 Agent 同款 Environment 段;工作目录取继承自会话策略的 sec,OS/日期/时区插件自取
            agent.Plugins.Register(new EnvContextPlugin { WorkingDirectoryProvider = _ => sec.WorkingDirectory });
            // 上下文压缩:跟随主 Agent 的压缩设定(宿主注入工厂;自动压缩关闭或宿主未接时返回 null,不压缩);
            // 压缩开始/完成进本运行帧日志(模态框与主聊天同款:压缩中齿轮行 → 完成后分割线)
            if (CompressionPluginFactory is { } compressionFactory
                && compressionFactory(ToolExecutionScope.SessionId, def.Provider, def.Model) is { } compression)
            {
                compression.CompressionStarted += _ => { run.MarkCompressionOpen(true); run.Publish("compression_started", null); };
                compression.CompressionFinished += _ => { run.MarkCompressionOpen(false); run.Publish("compression_finished", null); };
                agent.Plugins.Register(compression);
            }
            session = new AgentSession(client);
            run.AttachSession(session); // 运行中传话(sub_tell)的投递目标;收尾解除
            var callbacks = new SubAgentCallbacks(this, run, sec.WorkingDirectory)
            {
                // 每次请求的用量(含自动压缩摘要)交宿主记账:累计进发起会话,按子代理生效模型计价
                OnUsage = usage => UsageAccounting?.Invoke(ToolExecutionScope.SessionId, def.Provider, def.Model, usage),
            };
            var result = await agent.RunAsync(session, prompt, callbacks, sec, run.Token);
            run.Complete(string.IsNullOrWhiteSpace(result.Content) ? "(子代理未产出正文)" : result.Content);
        }
        catch (OperationCanceledException)
        {
            run.Cancel();
        }
        catch (Exception ex)
        {
            Log.Global.Error($"[SubAgent:{def.Name}] run #{run.Id} failed: {ex.Message}");
            run.Fail(ex.Message);
        }
        finally
        {
            if (session is not null)
                run.DetachSession(session); // 解除活会话引用并收口未决传话
            linked?.Dispose();
        }
    }

    /// <summary>
    /// 使用指引(注入主 Agent system prompt):定位句 → 可用子代理清单 → Rules 三条,
    /// 结构与 BiliLive/技能段对齐。工具全部禁用或无定义时不注入。
    /// </summary>
    public override string? GetSystemPromptSegment(AgentSession session)
    {
        if (ToolsAvailable is { } check && !check())
            return null;
        var defs = Definitions;
        if (defs.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Sub-agents: delegate self-contained sub-tasks to pre-configured specialists (sub_run / sub_wait / sub_tell / sub_list / sub_kill). A subagent runs an independent conversation with its own system prompt and toolset; it CANNOT see this conversation — the prompt you pass must contain ALL context it needs. Its final answer comes back as the sub_wait tool result.");
        sb.AppendLine("Available subagents:");
        foreach (var def in defs)
        {
            // 模型标注:显式供应商/模型供主模型判断子代理能力;继承(未单独指定模型)时不输出 <llm> 字段
            var llm = string.IsNullOrWhiteSpace(def.Model)
                ? null
                : (string.IsNullOrWhiteSpace(def.Provider) ? "" : def.Provider + "/") + def.Model;
            sb.AppendLine("<subagent>");
            sb.AppendLine($"<name>{def.Name}</name>");
            sb.AppendLine($"<description>{Describe(def)}</description>");
            sb.AppendLine($"<tools>{string.Join(", ", def.Tools)}</tools>");
            if (llm is not null)
                sb.AppendLine($"<llm>{llm}</llm>");
            sb.AppendLine("</subagent>");
        }
        sb.AppendLine("Rules:");
        sb.AppendLine("- Start one with sub_run(name, title, prompt): title is a short task name (≤12 chars) shown to the user; sub_run returns a run id immediately.");
        sb.AppendLine("- Block for the result with sub_wait(id); you may start several runs before waiting on any of them.");
        sb.AppendLine("- Use sub_tell(id, text) to pass mid-run instructions to a RUNNING subagent — steer, correct, or add context; it blocks until the subagent replies in text while it keeps working. Finished runs cannot be told.");
        sb.AppendLine("- Do not delegate trivial work you can do directly.");
        return sb.ToString();
    }

    /// <summary>子代理简介:系统提示词首行(截断),供模型判断何时委派。</summary>
    private static string Describe(Definition def)
    {
        var text = (def.SystemPrompt ?? "").Trim();
        if (text.Length == 0)
            return "no description";
        var line = text.Split('\n')[0].Trim();
        if (line.Length == 0)
            line = text.Length > 60 ? text[..57] + "…" : text;
        return line.Length > 60 ? line[..57] + "…" : line;
    }
}

/// <summary>子代理工具鉴权上下文:交给宿主出示卡片(投到主会话)并等待用户裁决。</summary>
public sealed record SubAgentAuthContext(
    int RunId, string RunName, string? MainSessionId,
    ToolCall ToolCall, ToolEffect Effect, AuthorizationResult Decision);
