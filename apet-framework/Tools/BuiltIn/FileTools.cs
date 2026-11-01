using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using APet.Framework.Models;
using APet.Framework.Security;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>读取本地文本文件,支持按行偏移/限量读取大文件。</summary>
public sealed class ReadFileTool : BuiltinToolBase
{
    private readonly FileToolOptions _options;

    public ReadFileTool(FileToolOptions? options = null)
    {
        _options = options ?? new FileToolOptions();
    }

    public override ToolDefinition Definition => ToolDefinition.Create(
        "read_file",
        "Read a local text file (returns JSON with content and line info)."
        + (_options.RootDirectory is null ? "" : $" Only files under {_options.RootDirectory} are allowed."),
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path (absolute or relative)" },
            "offset": { "type": "integer", "description": "Starting line number, 1-based, default 1" },
            "limit": { "type": "integer", "description": "Max number of lines to return, default all" }
          },
          "required": ["path"]
        }
        """,
        displayName: "读取文件");

    /// <summary>解析影响范围:读取目标文件。参数无法解析或越出根目录时按 Unknown 处理。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var fullPath = ResolvePath(_options.RootDirectory, doc.RootElement.GetProperty("path").GetString());
            return ToolEffect.Of(FileAccessKind.Read, fullPath);
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
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {fullPath}");

        var offset = root.TryGetProperty("offset", out var offsetEl) && offsetEl.TryGetInt32(out var o) ? Math.Max(1, o) : 1;
        int? limit = root.TryGetProperty("limit", out var limitEl) && limitEl.TryGetInt32(out var l) ? Math.Max(0, l) : null;

        var lines = File.ReadAllLines(fullPath, ToolJson.ResolveEncoding(_options.DefaultEncoding));
        var selected = lines.Skip(offset - 1);
        if (limit.HasValue)
            selected = selected.Take(limit.Value);

        var content = string.Join("\n", selected);
        var truncated = false;
        if (content.Length > _options.MaxChars)
        {
            content = content[.._options.MaxChars];
            truncated = true;
        }

        var result = new
        {
            path = fullPath,
            total_lines = lines.Length,
            offset,
            truncated,
            content,
        };
        return Task.FromResult(JsonSerializer.Serialize(result, ToolJson.Options));
    }
}

/// <summary>写入本地文本文件,支持覆盖与追加,自动创建目录。</summary>
public sealed class WriteFileTool : BuiltinToolBase
{
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FileToolOptions _options;

    public WriteFileTool(FileToolOptions? options = null)
    {
        _options = options ?? new FileToolOptions();
    }

    public override ToolDefinition Definition => ToolDefinition.Create(
        "write_file",
        "Write a local text file (UTF-8), creating missing parent directories."
        + (_options.RootDirectory is null ? "" : $" Only files under {_options.RootDirectory} are allowed."),
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path (absolute or relative)" },
            "content": { "type": "string", "description": "Full content to write" },
            "append": { "type": "boolean", "description": "Append to the end of the file if true; overwrite by default" }
          },
          "required": ["path", "content"]
        }
        """,
        displayName: "写入文件");

    /// <summary>解析影响范围:写入目标文件(含追加)。参数无法解析或越出根目录时按 Unknown 处理。</summary>
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
        var content = root.GetProperty("content").GetString() ?? "";
        var append = root.TryGetProperty("append", out var appendEl) && appendEl.ValueKind == JsonValueKind.True;

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (append)
            File.AppendAllText(fullPath, content, s_utf8NoBom);
        else
            File.WriteAllText(fullPath, content, s_utf8NoBom);

        var result = new
        {
            path = fullPath,
            appended = append,
            bytes_written = s_utf8NoBom.GetByteCount(content),
        };
        return Task.FromResult(JsonSerializer.Serialize(result, ToolJson.Options));
    }
}
