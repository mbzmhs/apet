using System.Text;
using System.Text.Json;
using APet.Framework.Logging;

namespace APet.Plugins.Mcp;

/// <summary>一台 MCP 服务器的工具信息(tools/list 结果)。</summary>
public sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema, bool ReadOnlyHint);

/// <summary>
/// MCP 单服务器连接(传输无关的编排层):initialize 握手 → tools/list → tools/call 透传。
/// 传输由类型决定:stdio(本地子进程,行分隔 JSON-RPC)/ http(Streamable HTTP)/ sse(HTTP+SSE 旧协议)。
/// 默认协议版本 2024-11-05("auto" 时使用);请求超时按配置毫秒数。
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _name;
    private readonly string _type;
    private readonly string _command;
    private readonly string _arguments;
    private readonly string _url;
    private readonly string _envJson;
    private readonly string _headersJson;
    private readonly Dictionary<string, string> _headers;
    private readonly string? _workingDirectory;
    private readonly int _timeoutMs;
    private readonly string _protocolVersion;
    private readonly bool _trustTools;
    private readonly ILog _log;

    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private McpTransport? _transport;

    /// <summary>配置里的服务器名(工具代理前缀/诊断用)。</summary>
    public string ServerName => _name;

    /// <summary>连接握手成功后的工具清单;未连接为空。</summary>
    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = Array.Empty<McpToolInfo>();

    /// <summary>握手时服务器下发的使用说明(initialize 响应的 instructions 字段;服务器未提供为 null)。</summary>
    public string? Instructions { get; private set; }

    /// <summary>最近一次连接/调用失败原因(供日志与诊断)。</summary>
    public string? LastError { get; private set; }

    public bool IsAlive => _transport is { } t && t.IsAlive;

    public McpServerConnection(string name, string type, string command, string arguments, string url,
        string envJson, string headersJson, string? workingDirectory, int timeoutMs, string protocolVersion, bool trustTools, ILog log)
    {
        _name = name;
        _type = string.IsNullOrWhiteSpace(type) ? "stdio" : type.Trim().ToLowerInvariant();
        _command = command;
        _arguments = arguments;
        _url = url;
        _envJson = envJson;
        _headersJson = headersJson ?? "";
        _headers = ParseHeaders(headersJson, log);
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        _timeoutMs = timeoutMs <= 0 ? 30000 : timeoutMs;
        _protocolVersion = string.IsNullOrWhiteSpace(protocolVersion) || protocolVersion == "auto"
            ? "2024-11-05"
            : protocolVersion.Trim();
        _trustTools = trustTools;
        _log = log;
    }

    /// <summary>与启动规格是否一致(类型/命令/参数/URL/环境变量/请求头/超时/协议版本/信任开关;名称由调用方比对)。
    /// 信任开关变化需要重连,以便按新信任重建代理工具的效果声明;请求头(鉴权)变化同样需要重连。</summary>
    public bool Matches(string type, string command, string arguments, string url, string envJson,
        string headersJson, string? workingDirectory, int timeoutMs, string protocolVersion, bool trustTools) =>
        _type == type && _command == command && _arguments == arguments && _url == url
        && _envJson == envJson && _headersJson == headersJson && (_workingDirectory ?? "") == (workingDirectory ?? "")
        && _timeoutMs == timeoutMs && _protocolVersion == protocolVersion
        && _trustTools == trustTools;

    /// <summary>解析附加请求头(JSON 对象);非法 JSON 按无头处理(等价匿名),不阻断连接。</summary>
    private static Dictionary<string, string> ParseHeaders(string? json, ILog log)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString()!;
        }
        catch (JsonException)
        {
            log.Debug("[MCP] headers JSON invalid, ignored: " + json);
        }
        return result;
    }

    /// <summary>建立传输并完成 initialize + tools/list 握手;失败抛异常(由调用方统一容错)。</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _transport = _type switch
        {
            "http" => new HttpTransport(_url, _timeoutMs, _headers),
            "sse" => new SseTransport(_url, _timeoutMs, _headers),
            _ => CreateStdioTransport(),
        };
        await _transport.StartAsync(ct);

        var init = await RequestAsync("initialize", new
        {
            protocolVersion = _protocolVersion,
            capabilities = new { },
            clientInfo = new { name = "APet", version = "2.0" },
        }, ct);
        var serverName = init.TryGetProperty("serverInfo", out var si) && si.TryGetProperty("name", out var sn)
            ? sn.GetString() ?? "?"
            : "?";
        Instructions = init.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.String
            ? ins.GetString()
            : null;
        _log.Info($"[MCP:{_name}] initialized server={serverName} protocol={_protocolVersion}");

        await NotifyAsync("notifications/initialized");

        var list = await RequestAsync("tools/list", null, ct);
        var tools = new List<McpToolInfo>();
        if (list.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString() ?? "";
                if (name.Length == 0)
                    continue;
                var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var schema = t.TryGetProperty("inputSchema", out var s) ? s.Clone()
                    : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
                // MCP 标准注解:annotations.readOnlyHint=true 表示服务器声明该工具只读(安全策略放行,不再询问)
                var readOnly = t.TryGetProperty("annotations", out var ann)
                    && ann.TryGetProperty("readOnlyHint", out var roh)
                    && roh.ValueKind == JsonValueKind.True;
                tools.Add(new McpToolInfo(name, desc, schema, readOnly));
            }
        }
        Tools = tools;
        _log.Info($"[MCP:{_name}] {tools.Count} tool(s) listed");
    }

    /// <summary>调用服务器工具;返回拼接后的文本结果(工具级错误以文本形式带回,不抛异常)。</summary>
    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!IsAlive)
                return ErrorJson($"MCP server '{_name}' is not running.");
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var result = await RequestAsync("tools/call", new { name = toolName, arguments = args.RootElement.Clone() }, ct);
            var sb = new StringBuilder();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && part.TryGetProperty("text", out var text))
                        sb.AppendLine(text.GetString());
                }
            }
            var isError = result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
            var body = sb.ToString().TrimEnd();
            if (isError)
                return ErrorJson(string.IsNullOrEmpty(body) ? "tool reported an error" : body);
            return string.IsNullOrEmpty(body) ? "(empty result)" : body;
        }
        catch (JsonException ex)
        {
            return ErrorJson("arguments must be a valid JSON object: " + ex.Message);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return ErrorJson("MCP call failed: " + ex.Message);
        }
    }

    /// <summary>发请求并等待响应;超时(配置毫秒)抛 TimeoutException。直传(http)与管道(stdio/sse)两种路由。</summary>
    private async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException($"MCP '{_name}' transport not started");
        var id = Interlocked.Increment(ref _nextId);
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }, s_json);

        if (transport.DirectMode)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            return await transport.RequestDirectAsync(message, timeout.Token);
        }

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
            _pending[id] = tcs;
        try
        {
            await transport.SendAsync(message, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeoutMs);
            // 有限延迟而非 Timeout.Infinite:响应先到时无限计时器永不解除,每次调用泄漏一枚
            var done = await Task.WhenAny(tcs.Task, Task.Delay(_timeoutMs, timeout.Token));
            if (done != tcs.Task)
                throw new TimeoutException($"MCP '{_name}' {method} timed out after {_timeoutMs}ms");
            return await tcs.Task;
        }
        finally
        {
            lock (_pending)
                _pending.Remove(id);
        }
    }

    /// <summary>管道模式响应路由(stdio 读循环 / SSE 事件流回调到这里)。</summary>
    private void RoutePipedResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return; // 通知:当前协议面无需处理
        var id = idEl.GetInt64();
        TaskCompletionSource<JsonElement>? tcs;
        lock (_pending)
            _pending.Remove(id, out tcs);
        if (tcs is null)
            return;
        if (root.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error";
            tcs.TrySetException(new InvalidOperationException($"MCP '{_name}' error: {msg}"));
        }
        else
        {
            tcs.TrySetResult(root.TryGetProperty("result", out var result)
                ? result.Clone()
                : JsonSerializer.SerializeToElement(new { }));
        }
    }

    private McpTransport CreateStdioTransport()
    {
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(_envJson))
        {
            using var doc = JsonDocument.Parse(_envJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("环境变量必须是 JSON 对象");
            foreach (var kv in doc.RootElement.EnumerateObject())
                env[kv.Name] = kv.Value.ToString();
        }
        return new StdioTransport(_command, _arguments, _workingDirectory, env, RoutePipedResponse, _log);
    }

    private async Task NotifyAsync(string method)
    {
        var transport = _transport ?? throw new InvalidOperationException($"MCP '{_name}' transport not started");
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", method }, s_json);
        using var cts = new CancellationTokenSource(_timeoutMs);
        await transport.SendAsync(message, cts.Token);
    }

    /// <summary>结束传输并清理(等待者由传输层收尾唤醒)。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_transport is not { } transport)
            return;
        _transport = null;
        try
        {
            await transport.DisposeAsync();
        }
        catch
        {
            // 收尾清理:任何异常都不外抛
        }
        lock (_pending)
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new InvalidOperationException($"MCP '{_name}' connection closed"));
            _pending.Clear();
        }
    }

    private static string ErrorJson(string message) =>
        JsonSerializer.Serialize(new { error = message, code = "mcp_error" }, s_json);
}
