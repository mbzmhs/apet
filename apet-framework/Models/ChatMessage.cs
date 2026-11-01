using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace APet.Framework.Models;

/// <summary>OpenAI 兼容多模态内容片段:text(文本)/ image_url(图片)/ file(通用文件,PDF、音视频等)。</summary>
public sealed class ChatContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("image_url")]
    public ImageUrlContent? ImageUrl { get; init; }

    [JsonPropertyName("file")]
    public FileContent? File { get; init; }

    public static ChatContentPart TextPart(string text) => new() { Type = "text", Text = text };

    public static ChatContentPart ImagePart(string url) => new() { Type = "image_url", ImageUrl = new ImageUrlContent(url) };

    public static ChatContentPart FilePart(string fileName, string dataUrl) =>
        new() { Type = "file", File = new FileContent(fileName, dataUrl) };

    /// <summary>
    /// 附件 data URL → 内容片段:位图走 image_url;PDF/音频/视频等走 file 片段
    /// (OpenAI 兼容协议的 file 输入;端点是否支持由该模型声明的媒体类型决定)。
    /// </summary>
    public static ChatContentPart FromAttachmentDataUrl(string dataUrl)
    {
        var mime = dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? dataUrl[5..].Split(';')[0].Split(',')[0]
            : "";
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ImagePart(dataUrl);
        var ext = mime switch
        {
            "application/pdf" => ".pdf",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            _ => "",
        };
        return FilePart("attachment" + ext, dataUrl);
    }
}

/// <summary>内容片段中的图片地址(data:image/...;base64,... 或 http(s) URL)。</summary>
public sealed class ImageUrlContent
{
    public ImageUrlContent() { }

    public ImageUrlContent(string url) => Url = url;

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";
}

/// <summary>内容片段中的通用文件(PDF/音视频):文件名 + data URL(OpenAI file 输入形状)。</summary>
public sealed class FileContent
{
    public FileContent() { }

    public FileContent(string fileName, string fileData)
    {
        FileName = fileName;
        FileData = fileData;
    }

    [JsonPropertyName("filename")]
    public string FileName { get; init; } = "";

    [JsonPropertyName("file_data")]
    public string FileData { get; init; } = "";
}

/// <summary>会话消息。字段直接对齐 OpenAI 兼容协议的 wire 格式。</summary>
[JsonConverter(typeof(ChatMessageJsonConverter))]
public sealed class ChatMessage
{
    public ChatMessage() { }

    public ChatMessage(ChatRole role, string? content)
    {
        Role = role;
        Content = content;
    }

    [JsonPropertyName("role")]
    public ChatRole Role { get; set; }

    /// <summary>正文文本。多模态消息(含图片)时仍保留纯文本部分,供估算/压缩/UI 使用。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>
    /// 多模态内容片段(OpenAI content 数组)。非空时序列化优先于 Content(以数组形式写入 content 字段);
    /// 反序列化时从数组解析,文本片段合并进 Content。随会话文件落盘,重启后图片不丢失。
    /// </summary>
    [JsonIgnore]
    public List<ChatContentPart>? ContentParts { get; set; }

    /// <summary>思考内容(OpenAI 兼容生态的 reasoning_content 字段)。</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    /// <summary>Assistant 消息发起的工具调用。</summary>
    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; set; }

    /// <summary>Tool 角色消息对应的调用 Id。</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>对话时记录的 Token 占用(0 = 未记录,按需估算);本地统计用,不发给模型、不落盘。</summary>
    [JsonIgnore]
    public int TokenCount { get; set; }

    /// <summary>宿主分配的消息标识(如 /undo 的 display↔context 精确匹配):随会话文件落盘,不发给模型
    /// (wire 映射见 OpenAiCompatibleClient.ToWireMessage,只写协议字段)。</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>消息标识(消息创建时生成;用户消息复用 clientId,与 display 气泡共用一个标识):
    /// 随会话文件落盘,不发给模型。旧会话文件无此字段时为 null(加载后宿主补生成)。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>消息创建时刻(用户=发送;助手=请求发起前;工具=结果写回):落盘,不发给模型。</summary>
    [JsonPropertyName("time_created")]
    public DateTime? TimeCreated { get; set; }

    /// <summary>消息完成时刻(助手=流结束;工具=结果写回):落盘,不发给模型。</summary>
    [JsonPropertyName("time_completed")]
    public DateTime? TimeCompleted { get; set; }

    /// <summary>本条消息(一次 LLM 请求)的服务端真实用量;仅助手消息携带:落盘,不发给模型。</summary>
    [JsonPropertyName("usage")]
    public MessageUsage? Usage { get; set; }

    /// <summary>结束原因(stop / tool_calls / length / content_filter / other);仅助手消息携带:落盘,不发给模型。</summary>
    [JsonPropertyName("finish")]
    public string? Finish { get; set; }

    /// <summary>生成本条消息的供应商显示名;仅助手消息携带(逐消息归因,会话中途换模型不丢):落盘,不发给模型。</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>生成本条消息的模型名;仅助手消息携带:落盘,不发给模型。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

/// <summary>一次 LLM 请求的服务端真实用量(LlmUsage 的落盘形态;字段名对齐 OpenAI usage)。</summary>
public sealed class MessageUsage
{
    public MessageUsage() { }

    public MessageUsage(int promptTokens, int completionTokens, int totalTokens, int cachedTokens = 0, int reasoningTokens = 0)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        TotalTokens = totalTokens;
        CachedTokens = cachedTokens;
        ReasoningTokens = reasoningTokens;
    }

    [JsonPropertyName("prompt")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("cached")]
    public int CachedTokens { get; set; }

    /// <summary>思考 tokens(服务器 completion_tokens_details.reasoning_tokens 原值留档)。
    /// 官方文档:已包含在 completion 内——只作逐消息单独查看,不得再叠加到任何输出累计/计费/TPS。</summary>
    [JsonPropertyName("reasoning")]
    public int ReasoningTokens { get; set; }
}

/// <summary>
/// ChatMessage 的 JSON 转换器:content 字段多态——ContentParts 非空时按 OpenAI content 数组
/// (text / image_url / file 片段)写入,否则写普通字符串;读取时数组解析为 ContentParts 且文本片段合并进
/// Content。role 兼容字符串("user")与数值(旧版会话文件)两种形式。
/// </summary>
public sealed class ChatMessageJsonConverter : JsonConverter<ChatMessage>
{
    public override ChatMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var message = new ChatMessage();

        if (root.TryGetProperty("role", out var role))
            message.Role = role.ValueKind switch
            {
                JsonValueKind.String => Enum.Parse<ChatRole>(role.GetString()!, ignoreCase: true),
                JsonValueKind.Number => (ChatRole)role.GetInt32(),
                _ => default,
            };

        if (root.TryGetProperty("content", out var content))
        {
            switch (content.ValueKind)
            {
                case JsonValueKind.String:
                    message.Content = content.GetString();
                    break;
                case JsonValueKind.Array:
                    var parts = new List<ChatContentPart>();
                    var text = new StringBuilder();
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var typeEl))
                            continue;
                        var kind = typeEl.GetString();
                        if (kind == "text" && part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(ChatContentPart.TextPart(textEl.GetString()!));
                            text.Append(textEl.GetString());
                        }
                        else if (kind == "image_url"
                                 && part.TryGetProperty("image_url", out var imgEl)
                                 && imgEl.ValueKind == JsonValueKind.Object
                                 && imgEl.TryGetProperty("url", out var urlEl)
                                 && urlEl.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(ChatContentPart.ImagePart(urlEl.GetString()!));
                        }
                        else if (kind == "file"
                                 && part.TryGetProperty("file", out var fileEl)
                                 && fileEl.ValueKind == JsonValueKind.Object
                                 && fileEl.TryGetProperty("file_data", out var fdEl)
                                 && fdEl.ValueKind == JsonValueKind.String)
                        {
                            // 会话文件落盘的 file 片段(用户附带的 PDF/音视频):还原以便再次发给模型
                            var name = fileEl.TryGetProperty("filename", out var fnEl) && fnEl.ValueKind == JsonValueKind.String
                                ? fnEl.GetString() ?? ""
                                : "attachment";
                            parts.Add(ChatContentPart.FilePart(name, fdEl.GetString()!));
                        }
                    }
                    if (parts.Count > 0)
                        message.ContentParts = parts;
                    message.Content = text.Length > 0 ? text.ToString() : null;
                    break;
            }
        }

        if (root.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            message.ReasoningContent = reasoning.GetString();

        if (root.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            message.ToolCalls = new List<ToolCall>();
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var call = new ToolCall();
                if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    call.Id = idEl.GetString() ?? "";
                if (tc.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        call.Name = nameEl.GetString() ?? "";
                    if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        call.ArgumentsJson = argsEl.GetString() ?? "";
                }
                message.ToolCalls.Add(call);
            }
        }

        if (root.TryGetProperty("tool_call_id", out var toolCallId) && toolCallId.ValueKind == JsonValueKind.String)
            message.ToolCallId = toolCallId.GetString();

        if (root.TryGetProperty("client_id", out var clientId) && clientId.ValueKind == JsonValueKind.String)
            message.ClientId = clientId.GetString(); // 宿主簿记标识:仅会话文件携带,旧文件无此字段时为 null

        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            message.Id = id.GetString();

        if (DateTime.TryParse(root.TryGetProperty("time_created", out var created) && created.ValueKind == JsonValueKind.String
                ? created.GetString() : null, out var timeCreated))
            message.TimeCreated = timeCreated;

        if (DateTime.TryParse(root.TryGetProperty("time_completed", out var completed) && completed.ValueKind == JsonValueKind.String
                ? completed.GetString() : null, out var timeCompleted))
            message.TimeCompleted = timeCompleted;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var usageInfo = new MessageUsage();
                if (usage.TryGetProperty("prompt", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    usageInfo.PromptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion", out var ct2) && ct2.ValueKind == JsonValueKind.Number)
                    usageInfo.CompletionTokens = ct2.GetInt32();
                if (usage.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number)
                    usageInfo.TotalTokens = tt.GetInt32();
                if (usage.TryGetProperty("cached", out var cached) && cached.ValueKind == JsonValueKind.Number)
                    usageInfo.CachedTokens = cached.GetInt32();
                if (usage.TryGetProperty("reasoning", out var rt) && rt.ValueKind == JsonValueKind.Number)
                    usageInfo.ReasoningTokens = rt.GetInt32();
                message.Usage = usageInfo;
            }

        if (root.TryGetProperty("finish", out var finish) && finish.ValueKind == JsonValueKind.String)
            message.Finish = finish.GetString();

        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            if (model.TryGetProperty("provider", out var mp) && mp.ValueKind == JsonValueKind.String)
                message.Provider = mp.GetString();
            if (model.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String)
                message.Model = mm.GetString();
        }

        return message;
    }

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role.ToString().ToLowerInvariant());

        if (value.ContentParts is { Count: > 0 })
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var part in value.ContentParts)
            {
                writer.WriteStartObject();
                writer.WriteString("type", part.Type);
                if (part.Type == "text")
                {
                    writer.WriteString("text", part.Text ?? "");
                }
                else if (part.Type == "file")
                {
                    writer.WriteStartObject("file");
                    writer.WriteString("filename", part.File?.FileName ?? "");
                    writer.WriteString("file_data", part.File?.FileData ?? "");
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteStartObject("image_url");
                    writer.WriteString("url", part.ImageUrl?.Url ?? "");
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        else if (value.Content is not null)
        {
            writer.WriteString("content", value.Content);
        }

        if (value.ReasoningContent is not null)
            writer.WriteString("reasoning_content", value.ReasoningContent);

        if (value.ToolCalls is { Count: > 0 })
        {
            writer.WritePropertyName("tool_calls");
            writer.WriteStartArray();
            foreach (var call in value.ToolCalls)
            {
                // 与 ToolCall 属性的 wire 形状保持一致:{id, type:"function", function:{name, arguments}}
                writer.WriteStartObject();
                writer.WriteString("id", call.Id ?? "");
                writer.WriteString("type", "function");
                writer.WriteStartObject("function");
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.ArgumentsJson);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (value.ToolCallId is not null)
            writer.WriteString("tool_call_id", value.ToolCallId);

        if (value.ClientId is not null)
            writer.WriteString("client_id", value.ClientId); // 宿主簿记标识:落盘供 /undo 匹配,LLM wire 不走本转换器

        // 宿主簿记字段:只落盘,LLM wire 不走本转换器(见 ToWireMessage 只写协议字段)
        if (value.Id is not null)
            writer.WriteString("id", value.Id);

        if (value.TimeCreated is { } created)
            writer.WriteString("time_created", created.ToString("o"));

        if (value.TimeCompleted is { } completed)
            writer.WriteString("time_completed", completed.ToString("o"));

        if (value.Usage is { } usage)
        {
            writer.WriteStartObject("usage");
            writer.WriteNumber("prompt", usage.PromptTokens);
            writer.WriteNumber("completion", usage.CompletionTokens);
            writer.WriteNumber("total", usage.TotalTokens);
            if (usage.CachedTokens > 0)
                writer.WriteNumber("cached", usage.CachedTokens);
            if (usage.ReasoningTokens > 0)
                writer.WriteNumber("reasoning", usage.ReasoningTokens);
            writer.WriteEndObject();
        }

        if (value.Finish is not null)
            writer.WriteString("finish", value.Finish);

        if (value.Model is not null || value.Provider is not null)
        {
            writer.WriteStartObject("model");
            writer.WriteString("provider", value.Provider ?? "");
            writer.WriteString("model", value.Model ?? "");
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
