using System.IO;
using System.Text.Json;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;
using APet.Framework.Tools.BuiltIn;

namespace APet.Plugins.Vision;

/// <summary>
/// view_image():从磁盘读取一张图片并以图片回传给多模态 LLM 查看(盘上截图、图表、照片等)。
/// 相对路径按会话工作目录解析;文件须存在且为常见图片格式(上限 20MB)。
/// 只读目标文件(Read 按文件所在目录鉴权),无文件系统影响。
/// </summary>
public sealed class ViewImageTool : BuiltinToolBase, IApetToolMultimodal
{
    private const long MaxBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, string> MimeByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".bmp"] = "image/bmp",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
    };

    public override ToolDefinition Definition => ToolDefinition.Create(
        "view_image",
        "Read an image file from disk and return it as an image for visual analysis. Use it to see pictures, charts or saved screenshots on disk.",
        """{"type":"object","properties":{"path":{"type":"string","description":"Image file path (absolute, or relative to the working directory)"}},"required":["path"]}""",
        displayName: "查看图片");

    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var full = ResolveFull(doc.RootElement.GetProperty("path").GetString());
            if (full is null)
                return ToolEffect.UnknownEffect;
            return ToolEffect.Of(FileAccessKind.Read, full);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException)
        {
            return ToolEffect.UnknownEffect;
        }
    }

    /// <summary>纯文本兜底路径(注册中心对多模态工具优先调用 ExecuteRichAsync)。</summary>
    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        Task.FromResult(Load(argumentsJson).Text);

    public Task<ToolResult> ExecuteRichAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        Task.FromResult(Load(argumentsJson));

    /// <summary>路径解析与内置文件工具同一口径:~ 展开、相对路径按会话工作目录。</summary>
    private static string? ResolveFull(string? path)
    {
        try
        {
            return ResolvePath(null, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ToolResult Load(string argumentsJson)
    {
        string? full;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            full = ResolveFull(doc.RootElement.GetProperty("path").GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException)
        {
            return ToolResult.FromText("Invalid arguments: 'path' is required.");
        }

        if (full is null)
            return ToolResult.FromText("Invalid arguments: 'path' is required.");
        if (!File.Exists(full))
            return ToolResult.FromText($"Image file not found: {full}");

        var ext = Path.GetExtension(full);
        if (!MimeByExt.TryGetValue(ext, out var mime))
            return ToolResult.FromText(
                $"{full} is not a supported image format. Supported: {string.Join(", ", MimeByExt.Keys)}.");

        var size = new FileInfo(full).Length;
        if (size > MaxBytes)
            return ToolResult.FromText($"Image {full} is {size / 1024 / 1024} MB, exceeds the 20 MB limit.");

        var bytes = File.ReadAllBytes(full);
        var dataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);

        var text = $"Image loaded: {full} ({size / 1024} KB";
        try
        {
            using var ms = new MemoryStream(bytes);
            using var probe = System.Drawing.Image.FromStream(ms);
            text += $", {probe.Width}×{probe.Height}";
        }
        catch
        {
            // 尺寸探测失败不影响回图
        }
        text += ").";
        return new ToolResult
        {
            Text = text,
            ImageDataUrls = [dataUrl],
        };
    }
}
