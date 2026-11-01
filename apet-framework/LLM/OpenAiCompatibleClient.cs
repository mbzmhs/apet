using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using APet.Framework.Logging;
using APet.Framework.Models;

namespace APet.Framework.LLM;

/// <summary>OpenAI 兼容协议客户端,通过 /chat/completions 完成流式与非流式对话。</summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly LlmClientOptions _options;
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly ILog _log;
    private readonly string _thinkingStyle;

    public OpenAiCompatibleClient(LlmClientOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new ArgumentException("LlmClientOptions.BaseUrl must not be empty.", nameof(options));
        _log = options.Log ?? Log.Global;
        // 思考字段样式:显式指定优先;未指定按旧版 SendThinkingField 开关映射
        var style = (options.ThinkingStyle ?? "").Trim().ToLowerInvariant();
        _thinkingStyle = style.Length > 0 ? style : (options.SendThinkingField ? "thinking" : "off");

        if (httpClient is null)
        {
            var handler = new HttpClientHandler();
            var proxyMode = (options.ProxyMode ?? "system").Trim();
            if (proxyMode.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                handler.UseProxy = false; // 不使用任何代理
            }
            else if (!proxyMode.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                // 自定义代理地址(如 http://127.0.0.1:7890)
                handler.Proxy = new System.Net.WebProxy(proxyMode);
            }
            // system:默认行为(HttpClientHandler 默认使用系统代理设置)
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
        else
        {
            _http = httpClient;
        }

        var baseUrl = options.BaseUrl.TrimEnd('/');
        _endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? baseUrl
            : baseUrl + "/chat/completions";
    }

    /// <summary>连接配置(宿主读取 provider/model 给助手消息盖模型归因)。</summary>
    public LlmClientOptions Options => _options;

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request, stream: false);
        _log.Info($"LLM request started model={body.Model} messages={request.Messages.Count} stream=false");
        using var message = CreateRequest(body);
        using var response = await _http.SendAsync(message, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _log.Error($"LLM request failed HTTP {(int)response.StatusCode}: {text}");
            throw new LlmException($"LLM request failed with HTTP {(int)response.StatusCode}: {text}");
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var choice = doc.RootElement.GetProperty("choices")[0];
            return new ChatResponse
            {
                Message = ParseMessage(choice.GetProperty("message")),
                FinishReason = ParseFinishReason(choice) ?? FinishReason.Other,
                Usage = ParseUsage(doc.RootElement),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            throw new LlmException($"Failed to parse LLM response: {text}", ex);
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request, stream: true);
        _log.Info($"LLM request started model={body.Model} messages={request.Messages.Count} stream=true");
        using var message = CreateRequest(body);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _log.Error($"LLM request failed HTTP {(int)response.StatusCode}: {error}");
            throw new LlmException($"LLM request failed with HTTP {(int)response.StatusCode}: {error}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                _log.Info("LLM streaming response finished [DONE]");
                yield break;
            }

            // 逐帧设防:个别兼容端点会夹带非 JSON 行/缺字段 chunk(如 tool_call 缺 index),
            // 裸异常会打死整轮对话——跳过坏帧继续读流(丢一帧增量远好于整轮失败)
            IEnumerator<LlmStreamEvent> events;
            try
            {
                events = ParseChunk(payload).GetEnumerator();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                       or ArgumentException or FormatException)
            {
                _log.Error($"LLM stream chunk skipped (malformed): {payload[..Math.Min(payload.Length, 120)]} // {ex.Message}");
                continue;
            }
            using (events)
            {
                while (true)
                {
                    LlmStreamEvent evt;
                    try
                    {
                        if (!events.MoveNext())
                            break;
                        evt = events.Current;
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
                                               or ArgumentException or FormatException)
                    {
                        // 迭代中途抛出(延迟执行的分支):同样只丢该帧
                        _log.Error($"LLM stream chunk skipped mid-parse: {ex.Message}");
                        break;
                    }
                    yield return evt;
                }
            }
        }
    }

    private ChatRequest BuildBody(ChatRequest request, bool stream)
    {
        var model = string.IsNullOrEmpty(request.Model) ? _options.Model : request.Model;
        if (string.IsNullOrEmpty(model))
            throw new LlmException("Model name is not specified. Provide ChatRequest.Model or LlmClientOptions.Model.");

        return new ChatRequest
        {
            Model = model,
            Messages = request.Messages,
            Tools = request.Tools is { Count: > 0 } ? request.Tools : null,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            Stream = stream,
        };
    }

    /// <summary>构造符合 OpenAI 协议的请求体(工具需包一层 type/function 结构;思考字段按供应商样式追加)。</summary>
    private object BuildWireBody(ChatRequest request)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(ToWireMessage).ToList(),
            ["stream"] = request.Stream,
        };
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters,
                },
            }).ToList();
        }
        if (request.Temperature is { } temp)
            body["temperature"] = temp;
        var maxTokens = request.MaxTokens ?? (_options.MaxTokens > 0 ? _options.MaxTokens : null);
        if (maxTokens is not null)
            body["max_tokens"] = maxTokens;
        if (request.Stream == true)
            body["stream_options"] = new { include_usage = true };
        AddThinkingFields(body);
        return body;
    }

    /// <summary>ChatMessage → OpenAI wire 消息:只含协议字段(role/content/reasoning_content/tool_calls/tool_call_id),
    /// ClientId 等宿主簿记字段不发给模型。content 多态与 ChatMessageJsonConverter 的写入规则一致;
    /// null 字段由 s_json 的 WhenWritingNull 省略(与转换器"为空跳过"行为一致)。</summary>
    private static object ToWireMessage(ChatMessage message) => new
    {
        role = message.Role.ToString().ToLowerInvariant(),
        content = message.ContentParts is { Count: > 0 }
            ? (object)message.ContentParts.Select(part => part.Type == "text"
                ? (object)new { type = "text", text = part.Text ?? "" }
                : part.Type == "file"
                    ? (object)new { type = "file", file = new { filename = part.File?.FileName ?? "", file_data = part.File?.FileData ?? "" } }
                    : new { type = "image_url", image_url = new { url = part.ImageUrl?.Url ?? "" } }).ToList()
            : (object?)message.Content,
        reasoning_content = message.ReasoningContent,
        tool_calls = message.ToolCalls is { Count: > 0 }
            ? message.ToolCalls.Select(call => new
            {
                id = call.Id ?? "",
                type = "function",
                function = new { name = call.Name, arguments = call.ArgumentsJson },
            }).ToList()
            : null,
        tool_call_id = message.ToolCallId,
    };

    /// <summary>
    /// 思考字段按供应商样式追加(端点异构:同一开关各家字段名不同,未知字段在严格端点会 400,故只发对应样式):
    /// "thinking" = thinking.type: none→disabled,其余档位→enabled(GLM、阿里百炼等);
    /// "enable_thinking" = Qwen 原生布尔开关: none→false,其余→true;
    /// "reasoning_effort" = OpenAI/llama.cpp 风格,携带具体强度: default(开启)按 high 发送,
    /// xhigh/max→high,minimal/low/medium/high 原样,none→不发送(该样式无法表达"关闭",由服务端默认);
    /// "all" = zcode 式全兼容: 同时发送 thinking/enable_thinking/reasoning_effort/reasoning.effort,
    /// 仅适合对未知字段宽容的端点(严格端点如 Gemini 兼容接口会 400);
    /// "off" = 完全不发送(历史默认行为,由服务端默认决定)。
    /// </summary>
    private static string MapEffort(string effort) => effort switch
    {
        "minimal" or "low" or "medium" or "high" => effort,
        _ => "high", // default(开启)与 xhigh/max 收敛到协议支持的最高档
    };

    private void AddThinkingFields(Dictionary<string, object?> body)
    {
        var effort = (_options.ReasoningEffort ?? "").Trim().ToLowerInvariant();
        var off = effort is "none" or "off"; // "off":历史取值,视同 none
        switch (_thinkingStyle)
        {
            case "thinking":
                body["thinking"] = new { type = off ? "disabled" : "enabled" };
                break;
            case "enable_thinking":
                body["enable_thinking"] = !off;
                break;
            case "reasoning_effort":
                if (off)
                    break; // 该样式无"关闭"表达:不发送,由服务端默认
                body["reasoning_effort"] = MapEffort(effort);
                break;
            case "all":
                body["thinking"] = new { type = off ? "disabled" : "enabled" };
                body["enable_thinking"] = !off;
                if (!off)
                {
                    body["reasoning_effort"] = MapEffort(effort);
                    body["reasoning"] = new { effort = MapEffort(effort) };
                }
                break;
            default:
                break; // off:不携带思考字段
        }
    }

    private HttpRequestMessage CreateRequest(ChatRequest body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(BuildWireBody(body), s_json), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        return message;
    }

    private static IEnumerable<LlmStreamEvent> ParseChunk(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        foreach (var usage in ParseUsageEvents(doc.RootElement))
            yield return usage;
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            yield break;

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta))
        {
            // 空串 delta 丢弃:部分 OpenAI 兼容端点(如 Aliyun)在思考 chunk 里附带 content:""
            // (首个 chunk 反向附带 reasoning_content:""),空增量若下发会打碎思考段/正文段
            if (delta.TryGetProperty("reasoning_content", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.String)
            {
                var rt = reasoning.GetString()!;
                if (rt.Length > 0)
                    yield return new ThinkingDeltaEvent(rt);
            }

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var ct = content.GetString()!;
                if (ct.Length > 0)
                    yield return new ContentDeltaEvent(ct);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    string? id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;

                    string? name = null;
                    string? argumentsDelta = null;
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            name = nameEl.GetString();
                        if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                            argumentsDelta = argsEl.GetString();
                    }

                    // index 是分片归并的键:个别端点缺省时按枚举位兜底,不因缺字段打死整帧
                    var idx = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                        ? idxEl.GetInt32()
                        : -1;
                    yield return new ToolCallDeltaEvent(idx, id, name, argumentsDelta);
                }
            }
        }

        var finish = ParseFinishReason(choice);
        if (finish.HasValue)
            yield return new FinishedEvent(finish.Value);
    }

    /// <summary>解析响应 usage 字段(流式/非流式共用);缺失返回 null。</summary>
    private static LlmUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var prompt = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        var total = usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : prompt + completion;
        // prompt_tokens_details.cached_tokens:命中 Prompt 缓存的部分(状态栏缓存命中率的分子)
        var cached = usage.TryGetProperty("prompt_tokens_details", out var ptd)
            && ptd.ValueKind == JsonValueKind.Object
            && ptd.TryGetProperty("cached_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0;
        // llama.cpp 把命中数放顶层 prompt_cache_hit_tokens(无 prompt_tokens_details 包裹):本地后端同样有缓存命中
        if (cached == 0 && usage.TryGetProperty("prompt_cache_hit_tokens", out var hit)
            && hit.ValueKind == JsonValueKind.Number)
            cached = hit.GetInt32();
        // completion_tokens_details.reasoning_tokens:思考 token 原值留档(官方文档:已含在 completion_tokens 内,不得再叠加)
        var reasoning = usage.TryGetProperty("completion_tokens_details", out var ctd)
            && ctd.ValueKind == JsonValueKind.Object
            && ctd.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : 0;
        if (prompt == 0 && completion == 0 && total == 0)
            return null;
        return new LlmUsage(prompt, completion, total, cached, 0, reasoning);
    }

    private static IEnumerable<LlmStreamEvent> ParseUsageEvents(JsonElement root)
    {
        var usage = ParseUsage(root);
        if (usage is not null)
            yield return new UsageEvent(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.CachedTokens, usage.ReasoningTokens);
    }

    private static ChatMessage ParseMessage(JsonElement message)
    {
        var role = Enum.Parse<ChatRole>(message.GetProperty("role").GetString()!, ignoreCase: true);
        var result = new ChatMessage { Role = role };

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            result.Content = content.GetString();

        if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            result.ReasoningContent = reasoning.GetString();

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            result.ToolCalls = new List<ToolCall>();
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var call = new ToolCall { Id = tc.GetProperty("id").GetString() ?? "" };
                if (tc.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        call.Name = nameEl.GetString() ?? "";
                    if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        call.ArgumentsJson = argsEl.GetString() ?? "";
                }
                result.ToolCalls.Add(call);
            }
        }

        return result;
    }

    private static FinishReason? ParseFinishReason(JsonElement choice)
    {
        if (!choice.TryGetProperty("finish_reason", out var fr) || fr.ValueKind != JsonValueKind.String)
            return null;
        return fr.GetString() switch
        {
            "stop" => FinishReason.Stop,
            "tool_calls" => FinishReason.ToolCalls,
            "length" => FinishReason.Length,
            "content_filter" => FinishReason.ContentFilter,
            _ => FinishReason.Other,
        };
    }
}
