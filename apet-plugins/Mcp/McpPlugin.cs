using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.Framework.Agent;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;

namespace APet.Plugins.Mcp;

/// <summary>
/// MCP(Model Context Protocol)客户端插件:按配置连接多台 stdio 传输的 MCP 服务器,
/// 把服务器工具以代理工具(mcp_服务器_工具)注册进 Agent 工具管线,调用经 tools/call 透传。
/// 连接在插件 Initialize(启用)时后台建立;Dispose/禁用即断开并注销全部代理工具。
/// </summary>
public sealed class McpPlugin : AgentPluginBase
{
    public override string Name => "Mcp";

    private readonly object _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1); // 序列化清单应用(Initialize 与宿主推送可能并发)
    private readonly List<McpServerConnection> _connections = new();
    private readonly Dictionary<string, McpToolProxy> _registered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _connectErrors = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ServerSpec> _servers = Array.Empty<ServerSpec>();
    private ToolRegistry? _tools;

    /// <summary>由宿主注入:代理工具注册/注销的目标注册中心。</summary>
    public ToolRegistry? Tools { get => _tools; set => _tools = value; }

    /// <summary>连接失败的服务器及其原因(成功/断开后清除);设置页状态展示用。</summary>
    public IReadOnlyDictionary<string, string> ConnectErrors
    {
        get
        {
            lock (_gate)
                return _connectErrors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>已连接服务器的诊断摘要(名称 → 状态/工具数/错误)。</summary>
    public IReadOnlyList<(string Server, bool Alive, int Tools, string? Error)> Status
    {
        get
        {
            lock (_gate)
                return _connections.Select(c => (c.ServerName, c.IsAlive, c.Tools.Count, c.LastError)).ToList();
        }
    }

    /// <summary>服务器说明注入:initialize 握手时服务器可下发 instructions(使用须知),有内容的连接按注册顺序
    /// 包进 &lt;mcp_instructions&gt;;无任何服务器提供时返回 null,不占位。</summary>
    public override string? GetSystemPromptSegment(AgentSession session)
    {
        var provided = new List<(string Name, string Text)>();
        lock (_gate)
        {
            foreach (var c in _connections)
                if (!string.IsNullOrWhiteSpace(c.Instructions))
                    provided.Add((c.ServerName, c.Instructions!));
        }
        if (provided.Count == 0)
            return null;
        var sb = new StringBuilder();
        sb.Append("Follow these MCP server instructions when using their tools:\n<mcp_instructions>");
        foreach (var (name, text) in provided)
            sb.Append("\n<server name=\"").Append(name).Append("\">\n").Append(text.Trim()).Append("\n</server>");
        sb.Append("\n</mcp_instructions>");
        return sb.ToString();
    }

    public override void Initialize()
    {
        // 配置由宿主在启用前经 ApplyServers 推入;此处统一(重)连
        _ = ApplyServersAsync(_servers);
    }

    public override void Dispose()
    {
        McpServerConnection[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _connections];
            _connections.Clear();
            _connectErrors.Clear();
            if (_tools is { } tools)
            {
                foreach (var name in _registered.Keys)
                    tools.Unregister(name);
            }
            _registered.Clear();
        }
        // 释放挪出锁外且经线程池执行:DisposeAsync 内含真实异步点(SSE 传输的 Task.WhenAny、
        // stdio 的 WaitForExit),UI 线程锁内 sync-over-async 会把延续投回被阻塞的同步上下文 → 死锁;
        // Task.Run 脱离同步上下文,这里最多阻塞到逐台释放完成(每台 SSE 型 ≤1s)
        Task.Run(async () =>
        {
            foreach (var c in snapshot)
            {
                try { await c.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Global.Error($"[MCP:{c.ServerName}] dispose failed: {ex.Message}"); }
            }
        }).GetAwaiter().GetResult();
    }

    /// <summary>宿主推送服务器清单(设置页保存/构造时):与当前连接比对,变更的断开重连。调用串行化。</summary>
    public async Task ApplyServersAsync(IReadOnlyList<ServerSpec> servers)
    {
        await _applyGate.WaitAsync();
        try
        {
            List<McpServerConnection> toDispose = new();
            lock (_gate)
            {
                _servers = servers;
                var wanted = servers.Where(s => s.Enabled).ToList();

                // 断开不再需要/配置变化的连接
                foreach (var existing in _connections.ToList())
                {
                    var spec = wanted.FirstOrDefault(w => w.Name == existing.ServerName);
                    if (spec is null || !existing.Matches(spec.Type, spec.Command, spec.Arguments, spec.Url,
                            spec.EnvJson, spec.HeadersJson, spec.WorkingDirectory, spec.TimeoutMs, spec.ProtocolVersion, spec.TrustTools))
                    {
                        _connections.Remove(existing);
                        _connectErrors.Remove(existing.ServerName); // 停用/变更:清除旧错误,重连会重新登记
                        toDispose.Add(existing);
                        UnregisterServerTools(existing.ServerName);
                    }
                }
            }
            foreach (var old in toDispose)
                await old.DisposeAsync();

            foreach (var spec in servers.Where(s => s.Enabled))
            {
                bool alive;
                lock (_gate)
                    alive = _connections.Any(c => c.ServerName == spec.Name
                        && c.Matches(spec.Type, spec.Command, spec.Arguments, spec.Url,
                            spec.EnvJson, spec.HeadersJson, spec.WorkingDirectory, spec.TimeoutMs, spec.ProtocolVersion, spec.TrustTools));
                if (!alive)
                    await ConnectOneAsync(spec);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task ConnectOneAsync(ServerSpec spec)
    {
        var connection = new McpServerConnection(spec.Name, spec.Type, spec.Command, spec.Arguments, spec.Url,
            spec.EnvJson, spec.HeadersJson, spec.WorkingDirectory, spec.TimeoutMs, spec.ProtocolVersion, spec.TrustTools, Log.Global);
        try
        {
            await connection.ConnectAsync();
            lock (_gate)
            {
                _connections.Add(connection);
                _connectErrors.Remove(spec.Name);
            }
            foreach (var tool in connection.Tools)
                RegisterProxy(connection, tool, spec.TrustTools);
            Log.Global.Info($"[MCP:{spec.Name}] connected, {connection.Tools.Count} tool(s) registered");
        }
        catch (Exception ex)
        {
            Log.Global.Error($"[MCP:{spec.Name}] connect failed: {ex.Message}");
            lock (_gate)
                _connectErrors[spec.Name] = ex.Message;
            await connection.DisposeAsync();
        }
    }

    private void RegisterProxy(McpServerConnection connection, McpToolInfo tool, bool trustTools)
    {
        if (_tools is null)
            return;
        var proxy = new McpToolProxy(connection.ServerName, tool, connection, trustTools);
        lock (_gate)
        {
            if (_registered.ContainsKey(proxy.Definition.Name))
                return; // 同名冲突(服务器间重名):先到先得
            _registered[proxy.Definition.Name] = proxy;
        }
        try
        {
            _tools.Register(proxy);
        }
        catch (Exception ex)
        {
            Log.Global.Error($"[MCP] register {proxy.Definition.Name} failed: {ex.Message}");
            lock (_gate)
                _registered.Remove(proxy.Definition.Name);
        }
    }

    private void UnregisterServerTools(string serverName)
    {
        if (_tools is null)
            return;
        lock (_gate)
        {
            var prefix = McpToolProxy.ToolPrefix(serverName);
            foreach (var name in _registered.Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _tools.Unregister(name);
                _registered.Remove(name);
            }
        }
    }

    /// <summary>一台服务器的启动规格(连接复用比对用)。</summary>
    public sealed record ServerSpec(
        string Name, string Type, string Command, string Arguments, string Url, string EnvJson,
        string HeadersJson, string? WorkingDirectory, int TimeoutMs, string ProtocolVersion, bool TrustTools, bool Enabled);
}

/// <summary>MCP 工具代理:定义来自 tools/list,调用透传 tools/call;协议名 mcp_{服务器}_{工具}。
/// 效果声明:服务器声明 readOnlyHint → Read(自动放行);宿主开启服务器级信任 → NoEffect(放行);
/// 其余 Unknown → 走安全策略与宿主确认(MCP 工具是外部黑盒,默认必须过闸)。</summary>
public sealed class McpToolProxy : ToolBase
{
    private readonly McpServerConnection _connection;
    private readonly McpToolInfo _tool;
    private readonly ToolDefinition _definition;
    private readonly bool _trustTools;

    public string ServerName { get; }

    public McpToolProxy(string serverName, McpToolInfo tool, McpServerConnection connection, bool trustTools)
    {
        ServerName = serverName;
        _tool = tool;
        _connection = connection;
        _trustTools = trustTools;
        _definition = ToolDefinition.Create(
            ToolPrefix(serverName) + Sanitize(tool.Name),
            string.IsNullOrWhiteSpace(tool.Description)
                ? $"MCP tool {serverName}.{tool.Name} (the server provided no description; check its input schema for arguments)"
                : tool.Description,
            JsonSerializer.Serialize(tool.InputSchema),
            $"MCP调用·{serverName}");
    }

    public override ToolDefinition Definition => _definition;

    /// <summary>按服务器信任开关与 MCP readOnlyHint 注解声明效果;不虚报(不把外部工具说成无害)。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson) =>
        _trustTools ? ToolEffect.NoEffect
        : _tool.ReadOnlyHint ? ToolEffect.Of(FileAccessKind.Read)
        : ToolEffect.UnknownEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        _connection.CallToolAsync(_tool.Name, argumentsJson, cancellationToken);

    internal static string ToolPrefix(string serverName) =>
        "mcp_" + Sanitize(serverName) + "_";

    internal static string Sanitize(string raw) =>
        Regex.Replace(raw.Trim(), "[^A-Za-z0-9_-]", "_");
}
