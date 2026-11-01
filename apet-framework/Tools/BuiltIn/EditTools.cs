using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>编辑类工具共用辅助。</summary>
internal static class EditToolHelpers
{
    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static string EditError(string message, string code) =>
        JsonSerializer.Serialize(new { error = message, code }, ToolJson.Options);

    internal static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    internal static string ReplaceFirst(string content, string oldValue, string newValue)
    {
        var index = content.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? content : content[..index] + newValue + content[(index + oldValue.Length)..];
    }

    /// <summary>读入待编辑文本并记住原文件是否带 BOM。此前读按配置编码、写一律 UTF-8 无 BOM:
    /// GBK 文件被静默转码成 UTF-8、带 BOM 的文件被剥 BOM——编辑器外观察即"文件莫名变了码"。</summary>
    internal static (string Content, bool HadBom) ReadForEdit(string fullPath, Encoding encoding)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var preamble = encoding.GetPreamble();
        var hadBom = preamble.Length > 0 && bytes.Length >= preamble.Length
                     && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble);
        return (hadBom ? encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length)
                       : encoding.GetString(bytes), hadBom);
    }

    /// <summary>按原编码与原 BOM 状态写回编辑结果(UTF-8 无 BOM 走既有 Utf8NoBom)。</summary>
    internal static void WriteBackAfterEdit(string fullPath, string content, Encoding encoding, bool hadBom)
    {
        if (encoding.CodePage != 65001)
        {
            File.WriteAllText(fullPath, content, encoding); // 非 UTF-8(如 gb18030):preamble 为空,原样写回
            return;
        }
        File.WriteAllText(fullPath, content, hadBom ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : Utf8NoBom);
    }
}

/// <summary>
/// 精确字符串替换编辑(参考 opencode 的 edit 工具):
/// 读取文件后把 old_string 替换为 new_string,默认要求唯一匹配,可设 replace_all。
/// </summary>
public sealed class EditFileTool : BuiltinToolBase
{
    private readonly FileToolOptions _options;

    public EditFileTool(FileToolOptions? options = null) => _options = options ?? new FileToolOptions();

    public override ToolDefinition Definition => ToolDefinition.Create(
        "edit_file",
        "Edit a local text file by exact string replacement: old_string is replaced with new_string."
        + " Read the file first and include enough surrounding context so old_string matches uniquely"
        + " (set replace_all=true to replace every occurrence). Returns JSON with path and replaced count.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path (absolute or relative)" },
            "old_string": { "type": "string", "description": "Exact text to be replaced" },
            "new_string": { "type": "string", "description": "Replacement text (empty string deletes the text)" },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence; default false (requires a unique match)" }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """,
        displayName: "编辑文件");

    /// <summary>解析影响范围:写入目标文件。参数无法解析时按 Unknown 处理。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var fullPath = ResolvePath(_options.RootDirectory, doc.RootElement.GetProperty("path").GetString());
            return ToolEffect.Of(FileAccessKind.Write, fullPath);
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
        var fullPath = ResolvePath(_options.RootDirectory, root.GetProperty("path").GetString());
        var oldString = root.GetProperty("old_string").GetString() ?? "";
        var newString = root.GetProperty("new_string").GetString() ?? "";
        var replaceAll = root.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

        if (oldString.Length == 0)
            throw new ArgumentException("old_string must not be empty.");

        var encoding = ToolJson.ResolveEncoding(_options.DefaultEncoding);
        var (content, hadBom) = EditToolHelpers.ReadForEdit(fullPath, encoding);
        var occurrences = EditToolHelpers.CountOccurrences(content, oldString);

        if (occurrences == 0)
            return Task.FromResult(EditToolHelpers.EditError(
                $"old_string not found in {fullPath}.", "not_found"));
        if (!replaceAll && occurrences > 1)
            return Task.FromResult(EditToolHelpers.EditError(
                $"old_string matches {occurrences} locations in {fullPath};"
                + " include more surrounding context or set replace_all=true.",
                "ambiguous_match"));

        var updated = replaceAll
            ? content.Replace(oldString, newString)
            : EditToolHelpers.ReplaceFirst(content, oldString, newString);
        EditToolHelpers.WriteBackAfterEdit(fullPath, updated, encoding, hadBom);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            path = fullPath,
            replaced = replaceAll ? occurrences : 1,
        }, ToolJson.Options));
    }
}

/// <summary>
/// 应用 unified diff 补丁(参考 opencode 的 patch 工具):
/// 解析 @@ -l,c +l,c @@ 风格的 hunk,按上下文匹配后应用增删改。
/// </summary>
public sealed partial class ApplyPatchTool : BuiltinToolBase
{
    private readonly FileToolOptions _options;

    public ApplyPatchTool(FileToolOptions? options = null) => _options = options ?? new FileToolOptions();

    public override ToolDefinition Definition => ToolDefinition.Create(
        "apply_patch",
        "Apply a unified diff patch to a local text file. The patch must contain one or more hunks"
        + " in unified diff format ('@@ -l,c +l,c @@' headers with ' ', '+' and '-' lines);"
        + " context lines must match the file exactly. Returns JSON with path and applied hunk count.",
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path (absolute or relative)" },
            "patch": { "type": "string", "description": "Unified diff patch content (hunks; ---/+++ headers optional)" }
          },
          "required": ["path", "patch"]
        }
        """,
        displayName: "应用补丁");

    /// <summary>解析影响范围:写入目标文件。参数无法解析时按 Unknown 处理。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var fullPath = ResolvePath(_options.RootDirectory, doc.RootElement.GetProperty("path").GetString());
            return ToolEffect.Of(FileAccessKind.Write, fullPath);
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
        var fullPath = ResolvePath(_options.RootDirectory, root.GetProperty("path").GetString());
        var patch = root.GetProperty("patch").GetString() ?? "";
        if (patch.Trim().Length == 0)
            throw new ArgumentException("patch must not be empty.");

        var hunks = ParseHunks(patch);
        if (hunks.Count == 0)
            return Task.FromResult(EditToolHelpers.EditError(
                "No unified diff hunks found (expected '@@ -l,c +l,c @@' headers).", "bad_patch"));

        var encoding = ToolJson.ResolveEncoding(_options.DefaultEncoding);
        var (raw, hadBom) = EditToolHelpers.ReadForEdit(fullPath, encoding);
        // 匹配在归一化(LF)行上进行,行尾格式对匹配透明;写回时还原原行尾,
        // 避免给 CRLF 文件打补丁后整个文件被静默改成 LF
        var eol = raw.Contains("\r\n") ? "\r\n" : "\n";
        var normalized = raw.Replace("\r\n", "\n");
        var hadTrailingNewline = normalized.EndsWith("\n");
        var trimmed = normalized.TrimEnd('\n');
        var lines = trimmed.Length == 0 ? new List<string>() : trimmed.Split('\n').ToList();

        for (var i = 0; i < hunks.Count; i++)
        {
            var hunk = hunks[i];
            var match = FindMatch(lines, hunk.OldLines, Math.Max(0, hunk.OldStart - 1));
            if (match < 0)
                return Task.FromResult(EditToolHelpers.EditError(
                    $"Hunk #{i + 1} context does not match the file content.", "patch_failed"));
            lines.RemoveRange(match, hunk.OldLines.Count);
            lines.InsertRange(match, hunk.NewLines);
        }

        EditToolHelpers.WriteBackAfterEdit(fullPath, string.Join(eol, lines) + (hadTrailingNewline && lines.Count > 0 ? eol : ""), encoding, hadBom);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            path = fullPath,
            hunks = hunks.Count,
        }, ToolJson.Options));
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();

    private sealed record Hunk(int OldStart, List<string> OldLines, List<string> NewLines);

    private static List<Hunk> ParseHunks(string patch)
    {
        var hunks = new List<Hunk>();
        Hunk? current = null;
        foreach (var rawLine in patch.Replace("\r\n", "\n").Split('\n'))
        {
            var header = HunkHeaderRegex().Match(rawLine);
            if (header.Success)
            {
                current = new Hunk(int.Parse(header.Groups[1].Value), new List<string>(), new List<string>());
                hunks.Add(current);
                continue;
            }
            if (current is null || rawLine.StartsWith('\\'))
                continue; // hunk 之前的文件头 / "\ No newline" 标记
            if (rawLine.StartsWith('+'))
                current.NewLines.Add(rawLine[1..]);
            else if (rawLine.StartsWith('-'))
                current.OldLines.Add(rawLine[1..]);
            else
            {
                // 上下文行(含被 trim 的空行)
                var context = rawLine.Length > 0 ? rawLine[1..] : "";
                current.OldLines.Add(context);
                current.NewLines.Add(context);
            }
        }
        return hunks;
    }

    /// <summary>从 start 起向后找 hunk 旧内容匹配位置;找不到再从头全局找;仍找不到返回 -1。</summary>
    private static int FindMatch(List<string> lines, List<string> oldLines, int start)
    {
        if (oldLines.Count == 0)
            return Math.Min(start, lines.Count); // 纯插入
        for (var i = start; i + oldLines.Count <= lines.Count; i++)
            if (MatchesAt(lines, oldLines, i))
                return i;
        for (var i = 0; i + oldLines.Count <= lines.Count; i++)
            if (MatchesAt(lines, oldLines, i))
                return i;
        return -1;
    }

    private static bool MatchesAt(List<string> lines, List<string> oldLines, int index)
    {
        for (var j = 0; j < oldLines.Count; j++)
            if (!string.Equals(lines[index + j], oldLines[j], StringComparison.Ordinal))
                return false;
        return true;
    }
}
