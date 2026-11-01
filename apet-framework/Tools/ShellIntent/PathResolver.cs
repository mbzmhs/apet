namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// 路径归一化:相对路径拼(虚拟)工作目录、~ 展开、MSYS /c/... 转 C:\、
/// Windows 盘符相对形式 C:file 拼该盘当前目录、环境变量展开(展开失败原样保留)。
/// </summary>
internal static class PathResolver
{
    /// <summary>解析为绝对路径;非法返回 null。</summary>
    public static string? Resolve(string workingDirectory, string token)
    {
        try
        {
            // 不做 ExpandEnvironmentVariables:进程环境≠shell 会话环境,且会把 %{http_code}、%%X
            // 等记号搅出幻影路径。含 % 的记号由分析器按"效果未知"处理,不进路径列表。
            // MSYS 伪设备(/dev/tty、/dev/urandom、/dev/fd/…):Windows 上无对应真实文件,不算路径
            if (token.StartsWith("/dev/", StringComparison.Ordinal) || token.StartsWith("/dev\\", StringComparison.Ordinal))
                return null;
            if (token.StartsWith('~'))
                token = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + token[1..];

            // MSYS/Git-Bash 风格单字母盘符路径:/c/Users/... 与 /c:/Users/... → C:\Users\...
            if (token.Length >= 3 && token[0] == '/' && char.IsLetter(token[1])
                && (token[2] == '/' || token[2] == '\\' || token[2] == ':'))
            {
                token = char.ToUpperInvariant(token[1]) + ":\\" + token[2..].TrimStart('/', '\\', ':');
            }

            // Git-Bash UNC 形式://server/share → \\server\share(与 Git-Bash 转换一致;MSYS 单斜杠 /c/ 已在上一步处理)
            if (OperatingSystem.IsWindows() && token.StartsWith("//") && token.Length > 2)
            {
                token = "\\\\" + token[2..].Replace('/', '\\');
            }

            if (OperatingSystem.IsWindows() && token.StartsWith('/') && token != "/")
            {
                // POSIX 绝对路径在 Windows 上的语义:
                // /tmp、/var/tmp 按 Git-Bash 惯例指用户临时目录
                if (token is "/tmp" or "/var/tmp"
                    || token.StartsWith("/tmp/") || token.StartsWith("/tmp\\")
                    || token.StartsWith("/var/tmp/") || token.StartsWith("/var/tmp\\"))
                {
                    var temp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Temp";
                    token = token.StartsWith("/var/tmp", StringComparison.Ordinal)
                        ? temp + token["/var/tmp".Length..]
                        : temp + token["/tmp".Length..];
                }
                else
                {
                    // 其余 POSIX 卷相对路径(/dev、/home/...):.NET 会落到宿主进程所在盘
                    // (APet 部署盘,如 G:\dev),与 shell 仿真环境脱节。锚定到虚拟工作目录所在盘。
                    // GetPathRoot 而非 [..3]:UNC 工作目录(\\server\share\...)的"根"不是三字符,
                    // 硬切会产出非法路径
                    var cwdFull = Path.GetFullPath(workingDirectory);
                    var root = Path.GetPathRoot(cwdFull);
                    token = (string.IsNullOrEmpty(root) ? cwdFull : root) + token.TrimStart('/', '\\');
                }
            }

            // Windows 盘符相对形式(C:file / C:dir):相对该盘当前目录。
            // 进程环境不记录每盘 cwd:同盘按本进程 cwd 近似,异盘按盘根近似(范围判定从宽)
            if (OperatingSystem.IsWindows()
                && token.Length >= 2 && char.IsLetter(token[0]) && token[1] == ':'
                && !Path.IsPathRooted(token))
            {
                var drive = token[..2].ToUpperInvariant() + "\\";
                var cwd = Path.GetFullPath(workingDirectory);
                var baseDir = cwd.StartsWith(drive, StringComparison.OrdinalIgnoreCase) ? cwd : drive;
                token = baseDir + (token.Length > 2 ? token[2..].TrimStart('/', '\\') : "");
            }

            var full = Path.IsPathRooted(token) ? token : Path.Combine(workingDirectory, token);
            return Path.GetFullPath(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or System.IO.PathTooLongException)
        {
            return null;
        }
    }
}
