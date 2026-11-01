using System.Drawing;

namespace APet.Plugins.ChatBubble;

/// <summary>气泡文字字形(以名称持久化于配置;映射到 GDI 的 FontStyle)。</summary>
public enum BubbleFontStyle
{
    Regular,
    Bold,
    Italic,
    BoldItalic,
}

/// <summary>
/// 对话气泡的完整样式(字体/颜色/形状),不可变值对象。
/// 全部取值经 <see cref="From"/> 统一夹取与解析(非法回退默认),插件设置与设置页控件两条来源
/// 走同一入口,保证任何配置(含手改 setting.json 的脏数据)都能渲染出正确、位置不漂移的气泡。
/// 颜色一律不透明(#RRGGBB):气泡是 Region 裁剪的普通窗体,半透明填充只会与窗体底色混色,
/// 整体透明请用不透明度(OpacityPercent)。
/// </summary>
public sealed record BubbleStyle
{
    // ───────── 默认值:插件设置初始值 / 设置页回退 / 旧配置缺字段时的落点(三处共用) ─────────
    public const string DefaultFontFamily = "Microsoft YaHei UI";
    public const double DefaultFontSize = 10;
    public const string DefaultFontStyle = nameof(BubbleFontStyle.Regular);
    public const string DefaultTextColor = "#1E1E24";
    public const string DefaultFillColor = "#FFFFFF";
    public const string DefaultBorderColor = "#2D2D34";
    public const double DefaultBorderWidth = 2;
    public const int DefaultCornerRadius = 14;
    public const int DefaultTailLength = 12;
    public const int DefaultMaxWidth = 320;
    public const int DefaultOpacityPercent = 100;

    public required string FontFamily { get; init; }
    public required double FontSizePt { get; init; }
    public required FontStyle FontStyle { get; init; }
    public required Color TextColor { get; init; }
    public required Color FillColor { get; init; }
    public required Color BorderColor { get; init; }
    public required float BorderWidth { get; init; }
    public required int CornerRadius { get; init; }
    public required int TailLength { get; init; }
    public required int MaxWidth { get; init; }
    /// <summary>整体不透明度(0.30–1.0),淡入淡出的目标值。</summary>
    public required double Opacity { get; init; }

    /// <summary>默认样式(与插件设置的初始值一致)。</summary>
    public static BubbleStyle Default { get; } = From(
        DefaultFontFamily, DefaultFontSize, DefaultFontStyle,
        DefaultTextColor, DefaultFillColor, DefaultBorderColor,
        DefaultBorderWidth, DefaultCornerRadius, DefaultTailLength, DefaultMaxWidth, DefaultOpacityPercent);

    /// <summary>
    /// 统一构造入口:字号/粗细/圆角/尾巴/宽度/不透明度夹取到合法范围,
    /// 字形与颜色解析(非法回退默认);fontStyle 为 BubbleFontStyle 名称(忽略大小写)。
    /// </summary>
    public static BubbleStyle From(string? fontFamily, double fontSize, string? fontStyle,
        string? textColor, string? fillColor, string? borderColor,
        double borderWidth, int cornerRadius, int tailLength, int maxWidth, int opacityPercent)
    {
        var style = Enum.TryParse<BubbleFontStyle>(fontStyle, ignoreCase: true, out var parsed)
            ? parsed
            : BubbleFontStyle.Regular;
        return new BubbleStyle
        {
            FontFamily = string.IsNullOrWhiteSpace(fontFamily) ? DefaultFontFamily : fontFamily.Trim(),
            FontSizePt = Math.Clamp(double.IsNaN(fontSize) ? DefaultFontSize : fontSize, 6, 48),
            FontStyle = style switch
            {
                BubbleFontStyle.Bold => FontStyle.Bold,
                BubbleFontStyle.Italic => FontStyle.Italic,
                BubbleFontStyle.BoldItalic => FontStyle.Bold | FontStyle.Italic,
                _ => FontStyle.Regular,
            },
            TextColor = ParseColor(textColor, FromHex(DefaultTextColor)),
            FillColor = ParseColor(fillColor, FromHex(DefaultFillColor)),
            BorderColor = ParseColor(borderColor, FromHex(DefaultBorderColor)),
            BorderWidth = (float)Math.Clamp(double.IsNaN(borderWidth) ? DefaultBorderWidth : borderWidth, 0, 10),
            CornerRadius = Math.Clamp(cornerRadius, 0, 40),
            TailLength = Math.Clamp(tailLength, 0, 40),
            MaxWidth = Math.Clamp(maxWidth, 160, 800),
            Opacity = Math.Clamp(opacityPercent, 30, 100) / 100.0,
        };
    }

    /// <summary>
    /// 解析颜色文本:#RGB / #RRGGBB / #ARGB / #AARRGGBB(# 可省略);解析失败返回 false。
    /// 气泡颜色一律不透明,alpha 位(若有)丢弃——整体透明请用不透明度设置。
    /// </summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        var s = (text ?? "").Trim().TrimStart('#');
        if (s.Length == 8)
            s = s[2..];                 // #AARRGGBB:丢弃 alpha 位
        else if (s.Length == 4)
            s = s[1..];                 // #ARGB:丢弃 alpha 位
        else if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]); // #RGB 展开成 #RRGGBB
        if (s.Length != 6)
            return false;

        Span<int> digits = stackalloc int[6];
        for (var i = 0; i < 6; i++)
        {
            var digit = HexDigit(s[i]);
            if (digit < 0)
                return false;
            digits[i] = digit;
        }
        color = Color.FromArgb(255, digits[0] * 16 + digits[1], digits[2] * 16 + digits[3], digits[4] * 16 + digits[5]);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>解析颜色,失败时回退 fallback。</summary>
    public static Color ParseColor(string? text, Color fallback) =>
        TryParseColor(text, out var color) ? color : fallback;

    /// <summary>转为 #RRGGBB(不透明色)。</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color FromHex(string hex) => TryParseColor(hex, out var color) ? color : default;
}
