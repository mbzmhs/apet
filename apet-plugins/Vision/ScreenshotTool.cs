using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;

namespace APet.Plugins.Vision;

/// <summary>
/// screenshot():截取桌面屏幕并以图片回传给多模态 LLM 查看。
/// 参数不填 = 主屏;整数 id = Screen.AllScreens 顺序的第 N 块屏(0 基)。
/// 按原始分辨率捕获后编码 JPEG(不做缩放,界面展开可看原图);无文件系统影响(NoEffect 直接放行)。
/// </summary>
public sealed class ScreenshotTool : ToolBase, IApetToolMultimodal
{
    private const int JpegQuality = 85;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "screenshot",
        "Capture a desktop screen and return it as an image for visual analysis. Use it to see what is currently displayed on the user's screen.",
        """{"type":"object","properties":{"id":{"type":"integer","description":"Zero-based monitor index in Screen.AllScreens order. Omit to capture the primary display."}},"required":[]}""",
        displayName: "屏幕截图");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    /// <summary>纯文本兜底路径(注册中心对多模态工具优先调用 ExecuteRichAsync)。</summary>
    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        Task.FromResult(Capture(argumentsJson).Text);

    public Task<ToolResult> ExecuteRichAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        Task.FromResult(Capture(argumentsJson));

    private static ToolResult Capture(string argumentsJson)
    {
        var screens = Screen.AllScreens;
        var index = 0;
        var hasExplicitId = false;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    index = idEl.GetInt32();
                    hasExplicitId = true;
                }
            }
            catch (JsonException)
            {
                // 参数非法时按主屏处理
            }
        }

        // 未显式指定 id → 截主显示器。AllScreens 的第 0 块不保证是主屏(多屏枚举顺序取决于系统),
        // 不能拿它当主屏;显式 id → 按 AllScreens 顺序取第 N 块(0 基),越界报错。
        Screen? target;
        if (hasExplicitId)
        {
            if (index < 0 || index >= screens.Length)
                return ToolResult.FromText(
                    $"Invalid screen id {index}: this system has {screens.Length} screen(s), valid range is 0-{screens.Length - 1}.");
            target = screens[index];
        }
        else
        {
            // PrimaryScreen 理论上可为 null(无显示设备);兜底取 AllScreens 第一块
            target = Screen.PrimaryScreen ?? screens.FirstOrDefault();
        }
        if (target is null)
            return ToolResult.FromText("No display available to capture.");

        var bounds = target.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        // 原始分辨率编码 JPEG(不缩放),界面展开即可看原图
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            // EncoderParameters/EncoderParameter 持有原生内存:不释放则每次截图泄漏一次,高频截图累积
            using var codec = new EncoderParameters(1)
            {
                Param = [new EncoderParameter(Encoder.Quality, (long)JpegQuality)],
            };
            bitmap.Save(ms, jpegCodec, codec);
            bytes = ms.ToArray();
        }

        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        var text = hasExplicitId
            ? $"Screenshot of display {index + 1} ({bounds.Width}×{bounds.Height}) captured."
            : $"Screenshot of the primary display ({bounds.Width}×{bounds.Height}) captured.";
        return new ToolResult
        {
            Text = text,
            ImageDataUrls = [dataUrl],
        };
    }
}
