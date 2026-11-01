namespace APet.Framework.Tools;

/// <summary>
/// 工具执行结果:文本 + 可选图片附件(data URL)。图片随 Tool 消息以多模态内容回传给 LLM,
/// 供支持视觉的模型直接查看(如截图工具);纯文本工具只填 Text。
/// </summary>
public sealed class ToolResult
{
    /// <summary>结果文本(始终回传给 LLM)。</summary>
    public string Text { get; init; } = "";

    /// <summary>图片附件(data:image/...;base64,...);空 = 纯文本结果。</summary>
    public IReadOnlyList<string> ImageDataUrls { get; init; } = Array.Empty<string>();

    public static ToolResult FromText(string text) => new() { Text = text };
}

/// <summary>
/// 可选扩展:返回多模态结果(文本 + 图片)的工具实现该接口,
/// ToolRegistry 会优先调用 ExecuteRichAsync 而非 IApetTool.ExecuteAsync。
/// </summary>
public interface IApetToolMultimodal
{
    Task<ToolResult> ExecuteRichAsync(string argumentsJson, CancellationToken cancellationToken = default);
}
