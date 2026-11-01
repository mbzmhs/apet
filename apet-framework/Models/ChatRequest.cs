using System.Text.Json.Serialization;

namespace APet.Framework.Models;

/// <summary>发往 LLM 的对话请求。</summary>
public sealed class ChatRequest
{
    /// <summary>模型名称,留空时使用客户端配置的默认模型。</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    public List<ToolDefinition>? Tools { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>单次生成的最大 Token 数(max_tokens);null = 不限制。压缩插件用它约束摘要长度。</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>是否流式,由客户端内部设置。</summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
}
