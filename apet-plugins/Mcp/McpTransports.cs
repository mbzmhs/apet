using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using APet.Framework.Logging;

namespace APet.Plugins.Mcp;

/// <summary>
/// MCP 传输基类:stdio / http(Streamable)/ sse(HTTP+SSE 旧协议)三实现。
/// 两种应答路由:管道模式(stdio/sse)响应从共享流到达,经 OnResponse 回调路由到编排层的等待表;
/// 直传模式(http)每个请求在自身的 HTTP 响应上等待结果(RequestDirectAsync)。
/// </summary>
internal abstract class McpTransport : IAsyncDisposable
{
    /// <summary>管道模式:一条带 id 的 JSON-RPC 响应到达时回调(编排层负责匹配等待者)。</summary>
    public Action<JsonElement>? OnResponse { get; set; }

    /// <summary>直传模式:http 类传输每个请求在自身响应上等待;管道模式(stdio/sse)经共享流路由。</summary>
    public virtual bool DirectMode => false;

    /// <summary>连接是否仍可用(子进程未退出 / 流未断)。</summary>
    public abstract bool IsAlive { get; }

    /// <summary>建立连接(stdio 启动子进程;http 无握手;sse 打开事件流并等待 endpoint)。</summary>
    public abstract Task StartAsync(CancellationToken ct);

    /// <summary>发出一条已序列化的 JSON-RPC 消息(通知或管道模式请求;不等待响应)。</summary>
    public abstract Task SendAsync(string json, CancellationToken ct);

    /// <summary>直传模式:发出请求并在自身响应上等待结果(解析出 result 或抛异常)。</summary>
    public virtual Task<JsonElement> RequestDirectAsync(string json, CancellationToken ct) =>
        throw new NotSupportedException("transport is not in direct mode");

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>stdio 传输:本地子进程,stdin/stdout 每行一条 JSON-RPC 消息(MCP stdio 约定)。</summary>
internal sealed class StdioTransport : McpTransport
{
    private readonly string _command;
    private readonly string _arguments;
    private readonly string? _workingDirectory;
    private readonly Dictionary<string, string> _env;
    private readonly Action<JsonElement> _onResponse;
    private readonly ILog _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1); // stdin 写串行化:并发工具调用两行 JSON 交错会让服务器解析失败
    private Process? _process;

    public StdioTransport(string command, string arguments, string? workingDirectory,
        Dictionary<string, string> env, Action<JsonElement> onResponse, ILog log)
    {
        _command = command;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
        _env = env;
        _onResponse = onResponse;
        _log = log;
    }

    public override bool IsAlive => _process is { HasExited: false };

    public override Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _command,
            Arguments = _arguments,
            WorkingDirectory = _workingDirectory ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var (k, v) in _env)
            psi.Environment[k] = v;
        // .cmd/.bat(npx.cmd 等)不能以 UseShellExecute=false 直接启动:交给 cmd 转发
        var ext = Path.GetExtension(_command);
        if (ext is ".cmd" or ".bat")
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/d /s /c \"{_command} {_arguments}\"".TrimEnd();
        }
        _process = Process.Start(psi) ?? throw new InvalidOperationException("process failed to start");
        _ = Task.Run(() => ReaderLoopAsync(_process));
        return Task.CompletedTask;
    }

    public override async Task SendAsync(string json, CancellationToken ct)
    {
        if (_process is not { HasExited: false } process)
            throw new InvalidOperationException("MCP process is not running");
        await _writeGate.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), ct);
            await process.StandardInput.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReaderLoopAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (line is null)
                    break;
                if (line.Length == 0 || !line.StartsWith('{'))
                    continue; // 空行/非 JSON 噪声(某些服务器会打印横幅)
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    _onResponse(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // 单行解析失败不影响连接
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug("[MCP:stdio] reader loop ended: " + ex.Message);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false } process)
            {
                try
                {
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1500))
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                }
            }
            _process?.Dispose();
        }
        catch
        {
            // 收尾清理:任何异常都不外抛
        }
        await ValueTask.CompletedTask;
    }
}

/// <summary>
/// Streamable HTTP 传输(type=http):每个 JSON-RPC 消息 POST 到服务器 URL;
/// 请求响应可在同一响应体(application/json)或 SSE 流(text/event-stream)中到达;
/// initialize 响应的 Mcp-Session-Id 头在后续请求中回传;通知期望 202。
/// </summary>
internal sealed class HttpTransport : McpTransport
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _url;
    private readonly int _timeoutMs;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private string? _sessionId;

    public HttpTransport(string url, int timeoutMs, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("http 类型必须填写 URL");
        _url = url.Trim();
        _timeoutMs = timeoutMs;
        _headers = headers;
    }

    public override bool IsAlive => true; // 无常驻流:每次请求独立判定

    public override bool DirectMode => true;

    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public override async Task<JsonElement> RequestDirectAsync(string json, CancellationToken ct)
    {
        using var resp = await PostAsync(json, expectBody: true, ct);
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
        string body;
        if (mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Streamable HTTP 允许响应以 SSE 流交付(部分端点行为):取首个事件的 data 帧
            body = await ReadFirstSseDataAsync(resp, ct);
        }
        else
        {
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { });
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error";
            throw new InvalidOperationException("MCP error: " + msg);
        }
        return root.TryGetProperty("result", out var result)
            ? result.Clone()
            : JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>读 SSE 响应流,拿到首个完整事件的 data 帧即返回(服务器可能保持流不关,读全文会挂起)。</summary>
    private static async Task<string> ReadFirstSseDataAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0)
                    return string.Join("\n", data);
                continue;
            }
            if (line.StartsWith(':') || line.StartsWith("event:")
                || line.StartsWith("id:") || line.StartsWith("retry:"))
                continue;
            if (!line.StartsWith("data:"))
                continue;
            var value = line["data:".Length..];
            if (value.StartsWith(" "))
                value = value[1..];
            data.Add(value);
        }
        return data.Count > 0 ? string.Join("\n", data) : "";
    }

    /// <summary>通知:POST 并忽略响应体(202/200 均可;失败不抛,通知缺失不影响协议推进)。</summary>
    public override async Task SendAsync(string json, CancellationToken ct)
    {
        try
        {
            using var resp = await PostAsync(json, expectBody: false, ct);
        }
        catch
        {
            // 通知发送失败:静默(服务器不可达会在下一个请求上暴露)
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string json, bool expectBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
            req.Headers.Add("Mcp-Session-Id", _sessionId);
        // 配置里的附加请求头(鉴权:Bearer PAT / API 密钥等);重名头跳过不覆盖协议头
        if (_headers is not null)
            foreach (var (key, value) in _headers)
                req.Headers.TryAddWithoutValidation(key, value);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (expectBody && !resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"MCP HTTP {(int)resp.StatusCode}: {Truncate(body)}");
        }
        // Streamable HTTP:initialize 响应带会话 id,后续请求回传
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var values))
            _sessionId = values.FirstOrDefault();
        if (expectBody)
        {
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("text/event-stream"))
                return resp; // 由调用方按事件流解析(RequestDirectAsync 之外的路径目前不需要)
        }
        return resp;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public override ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// HTTP+SSE 传输(type=sse,旧协议):GET 服务器 URL 打开事件流,首个 endpoint 事件给出
/// 消息 POST 端点;请求 POST 到该端点(期望 202),响应从事件流按 id 路由回来。
/// </summary>
internal sealed class SseTransport : McpTransport
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _url;
    private readonly int _timeoutMs;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly TaskCompletionSource<string> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _streamCts;
    private Task? _readerLoop;
    private HttpResponseMessage? _stream;
    private string? _postUrl;

    public SseTransport(string url, int timeoutMs, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("sse 类型必须填写 URL");
        _url = url.Trim();
        _timeoutMs = timeoutMs;
        _headers = headers;
    }

    public override bool IsAlive => _postUrl is not null && _readerLoop is { IsCompleted: false };

    public override async Task StartAsync(CancellationToken ct)
    {
        _streamCts = new CancellationTokenSource();
        using var getReq = new HttpRequestMessage(HttpMethod.Get, _url);
        ApplyHeaders(getReq);
        _stream = await _http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, _streamCts.Token);
        if (!_stream.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP SSE connect failed: HTTP {(int)_stream.StatusCode}");
        var contentType = _stream.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("text/event-stream"))
            throw new InvalidOperationException($"MCP SSE 期望 text/event-stream,实际 {contentType}");
        _readerLoop = Task.Run(() => ReaderLoopAsync(_stream));
        // 等待 endpoint 事件(消息 POST 端点)。用有限延迟:Timeout.Infinite 的挂起计时器
        // 在 endpoint 先到时永不解除,每次连接泄漏一枚计时器
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutMs);
        var done = await Task.WhenAny(_endpoint.Task, Task.Delay(_timeoutMs, timeout.Token));
        if (done != _endpoint.Task)
            throw new TimeoutException($"MCP SSE 未在 {_timeoutMs}ms 内收到 endpoint 事件");
        _postUrl = await _endpoint.Task;
    }

    public override async Task SendAsync(string json, CancellationToken ct)
    {
        if (_postUrl is null)
            throw new InvalidOperationException("MCP SSE endpoint not ready");
        using var req = new HttpRequestMessage(HttpMethod.Post, _postUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(req);
        using var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode is not (200 or 202 or 204))
            throw new InvalidOperationException($"MCP SSE POST failed: HTTP {(int)resp.StatusCode}");
    }

    /// <summary>附加配置请求头(鉴权);重名头跳过不覆盖。</summary>
    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (_headers is null)
            return;
        foreach (var (key, value) in _headers)
            request.Headers.TryAddWithoutValidation(key, value);
    }

    private async Task ReaderLoopAsync(HttpResponseMessage stream)
    {
        string? eventName = null;
        try
        {
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;
                if (line.Length == 0)
                {
                    eventName = null; // 事件边界
                    continue;
                }
                if (line.StartsWith(':'))
                    continue; // SSE 注释
                if (line.StartsWith("event:"))
                {
                    eventName = line["event:".Length..].Trim();
                    continue;
                }
                if (!line.StartsWith("data:"))
                    continue;
                var data = line["data:".Length..].Trim();
                if (eventName == "endpoint")
                {
                    // 相对端点 → 相对事件流 URL 解析
                    _endpoint.TrySetResult(new Uri(new Uri(_url), data).ToString());
                    continue;
                }
                if (data.Length == 0 || !data.StartsWith('{'))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    OnResponse?.Invoke(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // 单条解析失败不影响流
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 流断开:等待者由编排层 Dispose/超时唤醒
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            _streamCts?.Cancel();
            if (_readerLoop is { } loop)
                await Task.WhenAny(loop, Task.Delay(1000));
            _stream?.Dispose();
            _streamCts?.Dispose();
            _http.Dispose();
        }
        catch
        {
            // 收尾清理
        }
    }
}
