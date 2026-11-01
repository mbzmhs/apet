using System.Text;

namespace APet.Framework.Tools;

/// <summary>
/// 子进程输出按内容自动判定编码后解码。命令输出的编码因程序而异:
/// bash/构建工具/脚本多为 UTF-8,wsl.exe 等对重定向管道输出 UTF-16(LE),
/// 部分老程序按系统代码页(简中 = GBK)。固定任一编码都会让另一类输出乱码,
/// 故按字节内容判定(BOM → UTF-16 的 NUL 分布 → UTF-8 合法性 → 系统代码页)。
/// </summary>
public static class OutputTextDecoder
{
    /// <summary>判定并解码一(整段)输出。</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";
        var (enc, offset) = Detect(bytes);
        return enc.GetString(bytes, offset, bytes.Length - offset);
    }

    /// <summary>
    /// 判定编码,返回 (编码, BOM 起始偏移)。供流式读取锁定一次:
    /// 以输出起始样本判定,后续字节按该编码解码。
    /// </summary>
    public static (Encoding, int) Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false), 3);              // UTF-8 BOM
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode, 2);                 // UTF-16LE BOM
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode, 2);        // UTF-16BE BOM
        }

        // 无 BOM:UTF-16 靠 NUL 字节在高/低字节的分布识别
        // (ASCII 在 UTF-16LE 中高字节恒为 0x00,故奇数位置出现大量 NUL)
        if (bytes.Length >= 4)
        {
            int even = 0, odd = 0;
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] == 0)
                {
                    if ((i & 1) == 0) even++;
                    else odd++;
                }
            if (odd > even && odd * 8 > bytes.Length)
                return (Encoding.Unicode, 0);                 // UTF-16LE
            if (even > odd && even * 8 > bytes.Length)
                return (Encoding.BigEndianUnicode, 0);        // UTF-16BE
        }

        // 既非 UTF-16:优先按 UTF-8,非法则回退系统代码页(简中 = GBK)
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (ArgumentException)
        {
            return (SystemCodePageEncoding(), 0);
        }
    }

    /// <summary>系统默认代码页(简中 Windows = GBK/936);不可用时回退 UTF-8。</summary>
    private static Encoding? _systemCodePage;

    private static Encoding SystemCodePageEncoding()
    {
        if (_systemCodePage is not null)
            return _systemCodePage;
        if (!OperatingSystem.IsWindows())
            return _systemCodePage = Encoding.UTF8;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return _systemCodePage = Encoding.GetEncoding(
                System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return _systemCodePage = Encoding.UTF8;
        }
    }
}
