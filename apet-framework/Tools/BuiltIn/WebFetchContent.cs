using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>
/// web_fetch 结果后处理的可测单元:溢出落盘、JSON 结构摘要、HTML 大纲与按节读取。
/// 落盘是"web_fetch 可在固定缓存目录写内部文件"的受控例外——目录由宿主固定
/// (%LocalAppData%\APet\fetch-cache),不接受模型指定,不写入用户工作区,
/// 不受目录权限档位管辖;该例外在 README 安全模型中有显式记载。
/// 目录内容是抓取到的网页数据:只写、只读回,永不参与任何路径解析或执行。
/// </summary>
public static partial class WebFetchContent
{
    /// <summary>max_chars 参数的硬上限:模型可放宽单次视口,但不能撑爆上下文。</summary>
    public const int ViewportHardCap = 100_000;

    /// <summary>视口参数钳制:下限 1000 防无意义取值,上限 <see cref="ViewportHardCap"/>。</summary>
    public static int ClampViewport(int requested) => Math.Clamp(requested, 1_000, ViewportHardCap);

    /// <summary>溢出落盘文件名:fetch_{请求 URL 的 SHA1 前 8 位}.{ext}——
    /// 用请求 URL(而非重定向后的最终 URL)保证同一 URL 重复抓取天然幂等(同名覆盖)。</summary>
    public static string BuildSpillFileName(Uri requestUrl, string contentType, string format)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(requestUrl.ToString())));
        var ext = contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? ".json"
            : contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                ? (format == "html" ? ".html" : ".md")
            : ".txt";
        return $"fetch_{hash[..8].ToLowerInvariant()}{ext}";
    }

    /// <summary>完整正文落盘(临时文件 + 原子替换,并发同 URL 覆盖不撕裂)。
    /// 超过 spillMax 的部分丢弃并在文件尾如实标注;返回(落盘路径, 原始总长, 文件自身是否截断)。</summary>
    public static (string Path, long TotalChars, bool FileTruncated) WriteSpillFile(
        string directory, string fileName, string text, int spillMax)
    {
        Directory.CreateDirectory(directory);
        var full = Path.Combine(directory, fileName);
        var content = text;
        var fileTruncated = false;
        if (text.Length > spillMax)
        {
            content = text[..spillMax]
                + $"\n\n[APet web_fetch: file content truncated here, {text.Length - spillMax} more characters were discarded]";
            fileTruncated = true;
        }
        var temp = Path.Combine(directory, Path.GetRandomFileName());
        File.WriteAllText(temp, content);
        File.Move(temp, full, overwrite: true);
        return (full, text.Length, fileTruncated);
    }

    /// <summary>清理过期落盘文件(默认保留 7 天);目录不存在或单个删除失败均静默跳过。</summary>
    public static void CleanupOldFiles(string directory, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            var cutoff = DateTime.Now - maxAge;
            foreach (var file in Directory.EnumerateFiles(directory, "fetch_*"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // 标题行:行首 1~6 个 # 加空格(嵌套代码块里的 # 行也可能命中——大纲是近似视图,可接受)
    [GeneratedRegex("^(?<level>#{1,6})\\s+(?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();
}
