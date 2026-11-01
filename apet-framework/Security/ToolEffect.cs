namespace APet.Framework.Security;

/// <summary>
/// 一次工具调用的影响范围:访问方式 + 受影响的路径列表。
/// 每个工具通过 AnalyzeEffect 从参数中解析;解析不出来按 Unknown 处理。
/// </summary>
public sealed record ToolEffect
{
    public FileAccessKind Access { get; init; }

    /// <summary>受影响的路径(绝对路径)。</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>无文件系统影响。</summary>
    public static ToolEffect NoEffect { get; } = new() { Access = FileAccessKind.None };

    /// <summary>效果未知(无法从参数解析)。</summary>
    public static ToolEffect UnknownEffect { get; } = new() { Access = FileAccessKind.Unknown };

    public static ToolEffect Of(FileAccessKind access, params string[] paths) =>
        new() { Access = access, Paths = paths };
}
