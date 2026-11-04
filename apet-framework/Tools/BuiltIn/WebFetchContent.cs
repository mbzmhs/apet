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

    /// <summary>JSON 响应结构摘要(确定性生成,不引入 LLM):数组给 length + 对象项的 key 并集,
    /// 对象给各 key 的类型/长度(数组再展开一层 item_keys,共两层)。解析失败/超大/非 JSON 返回 null。</summary>
    public static Dictionary<string, object?>? TryBuildJsonShape(string text, string contentType)
    {
        var trimmed = text.TrimStart();
        var looksJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith('{') || trimmed.StartsWith('[');
        if (!looksJson || text.Length > 1_000_000)
            return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => ArrayShape(doc.RootElement),
                JsonValueKind.Object => ObjectShape(doc.RootElement),
                _ => null,
            };
        }
    }

    private static Dictionary<string, object?> ArrayShape(JsonElement array)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        string? nonObjectItemType = null;
        var scanned = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (++scanned > 1000)
                break;
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var p in item.EnumerateObject())
                    if (keys.Add(p.Name) && keys.Count >= 30)
                        break;
            else if (nonObjectItemType is null && item.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                nonObjectItemType = item.ValueKind.ToString().ToLowerInvariant();
            if (keys.Count >= 30)
                break;
        }
        var shape = new Dictionary<string, object?>
        {
            ["type"] = "array",
            ["length"] = array.GetArrayLength(),
        };
        if (keys.Count > 0)
            shape["item_keys"] = keys.ToList();
        else if (nonObjectItemType is not null)
            shape["item_type"] = nonObjectItemType;
        return shape;
    }

    private static Dictionary<string, object?> ObjectShape(JsonElement obj)
    {
        var keys = new List<object?>();
        foreach (var p in obj.EnumerateObject())
        {
            if (keys.Count >= 30)
                break;
            var entry = new Dictionary<string, object?> { ["name"] = p.Name };
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    entry["type"] = "array";
                    entry["length"] = p.Value.GetArrayLength();
                    var itemKeys = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var item in p.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                            continue;
                        foreach (var ip in item.EnumerateObject())
                            itemKeys.Add(ip.Name);
                        if (itemKeys.Count >= 30)
                            break;
                    }
                    if (itemKeys.Count > 0)
                        entry["item_keys"] = itemKeys.ToList();
                    break;
                case JsonValueKind.Object:
                    entry["type"] = "object";
                    entry["length"] = p.Value.EnumerateObject().Count();
                    break;
                default:
                    entry["type"] = p.Value.ValueKind.ToString().ToLowerInvariant();
                    break;
            }
            keys.Add(entry);
        }
        return new Dictionary<string, object?> { ["type"] = "object", ["keys"] = keys };
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
    /// <summary>markdown 大纲:h1~h3 标题行,上限 50 条,offset 为标题行在文本中的字符偏移。</summary>
    public static List<object?>? BuildOutline(string markdown)
    {
        var outline = new List<object?>();
        foreach (Match match in HeadingLine().Matches(markdown))
        {
            var level = match.Groups["level"].Value.Length;
            if (level > 3)
                continue;
            var text = match.Groups["text"].Value.Trim();
            if (text.Length == 0)
                continue;
            if (text.Length > 120)
                text = text[..120].TrimEnd() + "…";
            outline.Add(new Dictionary<string, object?>
            {
                ["level"] = level,
                ["text"] = text,
                ["offset"] = match.Index,
            });
            if (outline.Count >= 50)
                break;
        }
        return outline.Count > 0 ? outline : null;
    }

    /// <summary>按标题前缀提取节内容(到下一个同级或更高级标题为止,即到下一个 level ≤ 起始 level 的标题)。
    /// 返回(节内容, 同名匹配总数);无匹配返回 null。忽略大小写,可命中任意级别的标题。</summary>
    public static (string Content, int Matches)? ExtractSection(string markdown, string titlePrefix)
    {
        if (string.IsNullOrWhiteSpace(titlePrefix))
            return null;
        (int Level, int Offset, string Text)? first = null;
        var total = 0;
        foreach (Match match in HeadingLine().Matches(markdown))
        {
            var text = match.Groups["text"].Value.Trim();
            if (!text.StartsWith(titlePrefix.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            total++;
            first ??= (match.Groups["level"].Value.Length, match.Index, text);
        }
        if (first is null)
            return null;
        var (level, offset, _) = first.Value;
        var end = markdown.Length;
        foreach (Match match in HeadingLine().Matches(markdown))
        {
            if (match.Index <= offset)
                continue;
            if (match.Groups["level"].Value.Length <= level)
            {
                end = match.Index;
                break;
            }
        }
        return (markdown[offset..end].Trim(), total);
    }

    [GeneratedRegex("^(?<level>#{1,6})\\s+(?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingLine();
}
