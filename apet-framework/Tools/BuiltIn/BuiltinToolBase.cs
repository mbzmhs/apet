using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>内置工具共用的 JSON 序列化与编码解析工具。</summary>
internal static class ToolJson
{
    /// <summary>工具返回值序列化选项:保留非 ASCII 字符,LLM 可读性更好。</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static ToolJson()
    {
        // 支持按名称解析非 UTF-8 编码(如 Windows 中文系统的 gbk/936)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static Encoding ResolveEncoding(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Encoding.UTF8 : Encoding.GetEncoding(name!);
}

/// <summary>内置工具基类:提供路径约束与解析逻辑。</summary>
public abstract class BuiltinToolBase : ToolBase
{
    /// <summary>
    /// 解析并校验路径。指定 RootDirectory 时,相对路径基于其解析,
    /// 且禁止越出根目录(防止 LLM 读写任意位置)。
    /// </summary>
    protected static string ResolvePath(string? rootDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.");

        // bash 风格用户目录:~ 与 ~/... 展开为当前用户主目录(模型常按 shell 习惯写 ~/x)
        if (path == "~" || path is "~/" or "~\\"
            || path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path == "~" ? profile : profile + path[1..];
        }

        // MSYS/Git Bash 盘符路径(/c/Users/...、/c:/Users/... → C:\Users\...):不转会落到宿主进程盘幻影
        if (path.StartsWith('/') && path.Length >= 3
            && char.IsLetter(path[1]) && path[2] is '/' or '\\' or ':')
        {
            path = char.ToUpperInvariant(path[1]) + ":\\" + path[2..].TrimStart('/', '\\', ':');
        }

        // Git-Bash UNC 形式://server/share → \\server\share
        if (path.StartsWith("//") && path.Length > 2)
        {
            path = "\\\\" + path[2..].Replace('/', '\\');
        }

        // 相对路径的解析基准:工具根目录 > 会话工作目录(执行环境) > 进程目录
        var baseDirectory = rootDirectory ?? ToolExecutionScope.WorkingDirectory;
        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.Combine(baseDirectory ?? ".", path);
        fullPath = Path.GetFullPath(fullPath);

        if (!string.IsNullOrEmpty(rootDirectory))
        {
            var root = Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 与策略同一口径:字符串越界,或路径经 junction/symlink 穿透到根外,都算越界
            var allowed = !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                && fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !APet.Framework.Security.PathScope.EscapesViaReparsePoint(fullPath, root);
            if (!allowed)
                throw new UnauthorizedAccessException($"Path {fullPath} is outside the allowed root directory {root}.");
        }

        return fullPath;
    }
}
