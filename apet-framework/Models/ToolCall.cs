using System.Text.Json.Serialization;

namespace APet.Framework.Models;

/// <summary>一次工具调用(OpenAI tool calling 协议中的 function call)。</summary>
public sealed class ToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; set; } = new();

    /// <summary>工具名称。</summary>
    [JsonIgnore]
    public string Name
    {
        get => Function.Name;
        set => Function.Name = value;
    }

    /// <summary>工具参数,JSON 字符串形式,与协议保持一致。</summary>
    [JsonIgnore]
    public string ArgumentsJson
    {
        get => Function.Arguments;
        set => Function.Arguments = value;
    }
}

public sealed class ToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}
