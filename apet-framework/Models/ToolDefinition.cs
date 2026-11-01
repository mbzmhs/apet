using System.Text.Json;

namespace APet.Framework.Models;

/// <summary>提供给 LLM 的工具定义,Parameters 为标准 JSON Schema。</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>UI 展示用的可读名称(如"写入文件");空时回退到 Name。</summary>
    public string DisplayName { get; init; } = "";

    public JsonElement Parameters { get; init; }

    /// <summary>展示名称:优先 DisplayName,否则协议名。</summary>
    public string GetDisplayName() => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public static ToolDefinition Create(string name, string description, string parametersJson, string? displayName = null)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        return new ToolDefinition
        {
            Name = name,
            Description = description,
            DisplayName = displayName ?? "",
            Parameters = doc.RootElement.Clone(),
        };
    }
}
