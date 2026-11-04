using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>
/// 搜索类内置工具(纯只读):search_files 按正则/子串搜文件内容,find_files 按 glob 找文件。
/// 效果模型与 read_file 同轨:上报 Read(搜索根目录),各权限档位行为自动与读文件对齐;
/// 路径解析复用 FileToolOptions.RootDirectory 约束(为空时不限,与 read_file 一致)。
/// </summary>
public sealed class SearchFilesTool : BuiltinToolBase
{
    /// <summary>单次扫描的文件数上限:大归档/整个盘符的误调用不至于挂死工具循环。</summary>
    internal const int MaxScanFiles = 100_000;
    /// <summary>单文件扫描字节上限:更大的文件按"超大文件"跳过并如实计数(读整行需要整文件进内存)。</summary>
    internal const long MaxScanFileBytes = 8 * 1024 * 1024;
    /// <summary>匹配行文本截断长度(GBK 老代码常见超长行,防单行撑爆结果)。</summary>
    internal const int MaxMatchTextChars = 500;

    private readonly FileToolOptions _options;

    public SearchFilesTool(FileToolOptions? options = null)
    {
        _options = options ?? new FileToolOptions();
    }

    public override ToolDefinition Definition => ToolDefinition.Create(
        "search_files",
        "Search file CONTENTS under a directory (recursive) by regex or literal substring; returns matching lines."
        + " Binary files (NUL sniff) and files over 8MB are skipped. Encoding auto-detects BOM/UTF-8/GBK per file,"
        + " which suits legacy code in non-UTF-8 encodings."
        + (_options.RootDirectory is null ? " Default search root is the session working directory." : $" Only paths under {_options.RootDirectory} are allowed."),
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regex to match against line content (invalid regex falls back to literal substring)" },
            "path": { "type": "string", "description": "Directory to search (absolute or relative). Default: session working directory" },
            "glob": { "type": "string", "description": "Filename filter like *.go or **/*.frm; omitted = all files" },
            "context": { "type": "integer", "description": "Context lines around each match (0-5, default 0)" },
            "max_results": { "type": "integer", "description": "Max matching lines returned, default 50 (cap 500)" },
            "encoding": { "type": "string", "description": "auto (default) / utf-8 / gbk / any .NET encoding name" }
          },
          "required": ["pattern"]
        }
        """,
        displayName: "搜索文件内容");

    /// <summary>读取类效果:搜索根目录(与 read_file 同轨,档位行为一致)。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return ToolEffect.Of(FileAccessKind.Read, ResolveRoot(doc.RootElement));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            return ToolEffect.UnknownEffect;
        }
    }

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        var pattern = root.GetProperty("pattern").GetString() ?? "";
        if (pattern.Length == 0)
            throw new ArgumentException("Pattern must not be empty.");
        var rootPath = ResolveRoot(root);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Directory not found: {rootPath}");

        var glob = root.TryGetProperty("glob", out var globEl) ? globEl.GetString() ?? "" : "";
        var globRegex = glob.Length > 0 ? GlobToRegex(glob) : null;
        var context = root.TryGetProperty("context", out var ctxEl) && ctxEl.TryGetInt32(out var c) ? Math.Clamp(c, 0, 5) : 0;
        var maxResults = root.TryGetProperty("max_results", out var mrEl) && mrEl.TryGetInt32(out var mr) ? Math.Clamp(mr, 1, 500) : 50;
        var encodingMode = (root.TryGetProperty("encoding", out var encEl) ? encEl.GetString() : null) ?? "auto";

        // 正则优先;编译失败(非正则语法的普通子串)按字面量匹配——模型两种写法都能用
        Regex? regex = null;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException)
        {
        }

        var results = new List<object?>();
        var totalMatches = 0L;
        var scanned = 0;
        var skipped = 0;
        foreach (var file in EnumerateFiles(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned >= MaxScanFiles)
                break;
            // glob 同时匹配相对路径与文件名:*.go 命中任意层级的文件名,**/*.go 命中相对路径
            if (globRegex is not null)
            {
                var relPath = file[(rootPath.Length + 1)..].Replace('\\', '/');
                if (!globRegex.IsMatch(relPath) && !globRegex.IsMatch(Path.GetFileName(file)))
                    continue;
            }
            scanned++;
            var info = new FileInfo(file);
            if (info.Length > MaxScanFileBytes || LooksBinary(file, info.Length))
            {
                skipped++;
                continue;
            }
            List<string> lines;
            try
            {
                lines = ReadLines(file, encodingMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                skipped++;
                continue;
            }
            for (var i = 0; i < lines.Count; i++)
            {
                var hit = regex is not null ? regex.IsMatch(lines[i]) : lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (!hit)
                    continue;
                totalMatches++;
                if (results.Count < maxResults)
                    results.Add(MakeMatch(file, lines, i, context, rootPath));
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["root"] = rootPath,
            ["total_matches"] = totalMatches,
            ["scanned_files"] = scanned,
            ["skipped_files"] = skipped,
            ["truncated"] = totalMatches > results.Count,
            ["results"] = results,
        };
        return Task.FromResult(JsonSerializer.Serialize(payload, ToolJson.Options));
    }

    private string ResolveRoot(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
            path = _options.RootDirectory ?? ToolExecutionScope.WorkingDirectory
                ?? Environment.CurrentDirectory;
        return ResolvePath(null, path); // 根目录约束由枚举起点承担;绝对路径不越界判定与 read_file 同口径
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // 跳过重解析点(junction/symlink):防环路与越出搜索根
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 32,
        });

    private static Dictionary<string, object?> MakeMatch(string file, IReadOnlyList<string> lines, int index, int context, string rootPath)
    {
        var from = Math.Max(0, index - context);
        var to = Math.Min(lines.Count, index + context + 1);
        var entry = new Dictionary<string, object?>
        {
            ["file"] = file,
            ["line"] = index + 1,
            ["text"] = lines[index].Length > MaxMatchTextChars
                ? lines[index][..MaxMatchTextChars] + "…"
                : lines[index],
        };
        if (context > 0)
        {
            var ctx = new List<object?>();
            for (var i = from; i < to; i++)
                if (i != index)
                    ctx.Add(new Dictionary<string, object?> { ["line"] = i + 1, ["text"] = TruncateCtx(lines[i]) });
            entry["context"] = ctx;
        }
        return entry;
    }

    private static string TruncateCtx(string line) =>
        line.Length > MaxMatchTextChars ? line[..MaxMatchTextChars] + "…" : line;

    /// <summary>二进制嗅探:读前 8KB,出现 NUL 字节即视为二进制(文本文件不含 NUL)。</summary>
    private static bool LooksBinary(string file, long length)
    {
        if (length == 0)
            return false;
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> buffer = stackalloc byte[8192];
            var read = stream.Read(buffer);
            for (var j = 0; j < read; j++)
                if (buffer[j] == 0)
                    return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // 读不动的一律按跳过处理
        }
    }

    /// <summary>按模式读行:显式编码直接用;auto 按字节嗅探 BOM/UTF-16,再以严格 UTF-8 试解,
    /// 失败(非法序列)回退 GBK——中文系统上的老代码归档多为此形态。</summary>
    private static List<string> ReadLines(string file, string encodingMode)
    {
        if (!encodingMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllLines(file, ToolJson.ResolveEncoding(encodingMode)).ToList();

        using var stream = File.OpenRead(file);
        Span<byte> head = stackalloc byte[4];
        var headLen = stream.Read(head);
        stream.Position = 0;
        Encoding encoding;
        if (headLen >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        else if (headLen >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            encoding = Encoding.Unicode;
        else if (headLen >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            encoding = Encoding.BigEndianUnicode;
        else
            encoding = Encoding.UTF8;

        try
        {
            if (ReferenceEquals(encoding, Encoding.UTF8) && !IsProbablyUtf8(stream))
            {
                // 无 BOM 且非严格 UTF-8:按 GBK 读(严格 UTF-8 试解失败即回退)
                stream.Position = 0;
                return File.ReadAllLines(file, Encoding.GetEncoding("gbk")).ToList();
            }
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding);
            return ReadAllLines(reader);
        }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            return File.ReadAllLines(file, Encoding.GetEncoding("gbk")).ToList();
        }
    }

    /// <summary>严格 UTF-8 试解(带 BOM 判定后调用):返回 false 表示字节流不是合法 UTF-8。</summary>
    private static bool IsProbablyUtf8(FileStream stream)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            using var reader = new StreamReader(stream, strict, detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096, leaveOpen: true);
            while (reader.ReadLine() is not null)
            {
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // 读不动按原编码继续,外层再兜
        }
    }

    private static List<string> ReadAllLines(StreamReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>glob → 正则:** 跨任意层级,* 与 ? 不跨分隔符;其余字符字面量,忽略大小写。</summary>
    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^", glob.Length * 2 + 8);
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*')
                sb.Append("[^/\\\\]*");
            else if (c == '?')
                sb.Append("[^/\\\\]");
            else
                sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>按 glob 模式找文件(纯只读,递归):返回绝对路径列表与命中总数。</summary>
public sealed class FindFilesTool : BuiltinToolBase
{
    /// <summary>单次枚举条目上限:防误指向整盘时挂死(枚举到此即止,结果如实标注不完整)。</summary>
    internal const int MaxEnumerateEntries = 200_000;

    private readonly FileToolOptions _options;

    public FindFilesTool(FileToolOptions? options = null)
    {
        _options = options ?? new FileToolOptions();
    }

    public override ToolDefinition Definition => ToolDefinition.Create(
        "find_files",
        "Find files under a directory (recursive) by glob pattern like **/*.frm or *.go; returns absolute paths."
        + (_options.RootDirectory is null ? " Default root is the session working directory." : $" Only paths under {_options.RootDirectory} are allowed."),
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob pattern matched against the path relative to the search root (e.g. **/*.frm, *.go, src/**/test_*.py)" },
            "path": { "type": "string", "description": "Directory to search (absolute or relative). Default: session working directory" },
            "max_results": { "type": "integer", "description": "Max paths returned, default 200 (cap 1000)" }
          },
          "required": ["pattern"]
        }
        """,
        displayName: "查找文件");

    /// <summary>读取类效果:搜索根目录(与 read_file 同轨)。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return ToolEffect.Of(FileAccessKind.Read, ResolveRoot(doc.RootElement));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            return ToolEffect.UnknownEffect;
        }
    }

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        var pattern = root.GetProperty("pattern").GetString() ?? "";
        if (pattern.Length == 0)
            throw new ArgumentException("Pattern must not be empty.");
        var rootPath = ResolveRoot(root);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Directory not found: {rootPath}");

        var regex = SearchFilesTool.GlobToRegex(pattern);
        var maxResults = root.TryGetProperty("max_results", out var mrEl) && mrEl.TryGetInt32(out var mr)
            ? Math.Clamp(mr, 1, 1000)
            : 200;

        var results = new List<object?>();
        var totalMatches = 0L;
        var scanned = 0;
        var truncated = false;
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                     MaxRecursionDepth = 32,
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned >= FindFilesTool.MaxEnumerateEntries)
            {
                truncated = true;
                break;
            }
            // 相对路径形态参与匹配:pattern 是相对搜索根的路径模式
            var rel = file[(rootPath.Length + 1)..].Replace('\\', '/');
            if (!regex.IsMatch(rel) && !regex.IsMatch(Path.GetFileName(file).Replace('\\', '/')))
                continue;
            totalMatches++;
            if (results.Count < maxResults)
                results.Add(file);
        }

        var payload = new Dictionary<string, object?>
        {
            ["root"] = rootPath,
            ["pattern"] = pattern,
            ["total_matches"] = totalMatches,
            ["scanned_entries"] = scanned,
            ["truncated"] = truncated || totalMatches > results.Count,
            ["results"] = results,
        };
        return Task.FromResult(JsonSerializer.Serialize(payload, ToolJson.Options));
    }

    private string ResolveRoot(JsonElement args)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
            path = _options.RootDirectory ?? ToolExecutionScope.WorkingDirectory
                ?? Environment.CurrentDirectory;
        return ResolvePath(null, path);
    }
}
