using APet.Framework.Security;

namespace APet.Framework.Tools.ShellIntent;

/// <summary>解析器公共部分:参数/重定向 → 绝对路径收集(去重、上限、空设备/URI 排除)。</summary>
public abstract class IntentAnalyzerBase : IIntentAnalyzer
{
    private const int MaxPaths = 32;

    /// <summary>纯变量/命令引用:$HOME、${x}、$(cmd)、`cmd` 展开结果不可静态定值。</summary>
    private static readonly System.Text.RegularExpressions.Regex VarRefRegex =
        new(@"^(\$[A-Za-z_][A-Za-z0-9_]*|\$\{[^}]*\}|\$\([^)]*\)|`[^`]*`)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public abstract IntentResult Analyze(string command, string workingDirectory);

    protected sealed class CommandSegment
    {
        public required CommandTokens Tokens { get; init; }
        public List<string> Segment = [];
        public bool IsPipe { get; init; }
    }

    /// <summary>子类产出命令段后统一收尾:路径收集 + 不确定记号 + 空路径兜底(CWD)。</summary>
    protected IntentResult Finish(IntentResult result, IReadOnlyList<CommandSegment> segments, string workingDirectory)
    {
        // 重定向参与定类(此前只按命令词定类,重定向目标仅作路径收集):
        // 输出重定向 = 真实写入,命令至少是 Write(输入重定向 < 不提升写);
        // 目标含 $ / % 等未展开记号 → 展开结果不可静态定值,整条转 Uncertain 交宿主
        // (此前只扫命令参数不扫重定向目标:cat a > $HOME/x 曾以 Read 级别静默放行)
        foreach (var intent in result.Commands)
        {
            foreach (var r in intent.Tokens.Redirects)
            {
                if (!r.Input && intent.Class != CommandClass.Uncertain)
                    intent.Class = CommandClass.Write;
                if (r.Target.Contains('$') || r.Target.Contains('%'))
                {
                    result.HasUncertain = true;
                    result.UncertainPaths.Add(r.Target);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? raw, string cwdAt, CommandClass? cls)
        {
            if (raw is null || raw.Length == 0 || result.Paths.Count >= MaxPaths)
                return;
            // {} 等记号组合(curl 的 %{http_code}):不是路径
            if (raw.Contains('{') || raw.Contains('}'))
                return;
            // 含 % 的记号(%%B、%VAR% 未展开等):展开结果不可静态定值,不拼幻影路径
            if (raw.Contains('%'))
                return;
            // 含 $ 的记号($B/api 变量+后缀、$() 命令替换):展开结果不可静态定值
            // (bash/cmd 分析器已把含 $ 的命令整条标记 Uncertain)
            if (raw.Contains('$'))
                return;
            // 通配符模式(*proc、*.png 之类):是匹配模式不是具体文件(Windows/NTFS 文件名不含 * ?)
            // 前缀目录若无通配则记为该目录(检索的真实访问面,如 ~/Desktop/Snip*_*.png → ~/Desktop)
            if (raw.Contains('*') || raw.Contains('?'))
            {
                var cut = raw.LastIndexOfAny(new[] { '/', '\\' });
                if (cut <= 0)
                    return;
                var dirPart = raw[..cut];
                if (dirPart.Contains('*') || dirPart.Contains('?') || dirPart.Contains('$')
                    || dirPart.Contains('%') || dirPart.Contains('{') || dirPart.Contains('}')
                    || dirPart.StartsWith("/dev", StringComparison.Ordinal))
                    return;
                raw = dirPart; // 后续按普通目录路径解析(~ 展开等)
            }
            // 裸根路径(rm -rf /、find /):按工作目录所在盘的盘根解析——命令确实触碰整盘,
            // 直接丢弃会让"无其他路径"退化成 CWD 兜底(rm -rf / 曾因此被当作只写工作目录放行)
            if (raw is "/" or "\\")
            {
                var driveRoot = DriveRootOf(cwdAt);
                if (driveRoot is null)
                    return;
                raw = driveRoot;
            }
            // 裸盘符(G:):该盘当前目录不可静态推演,按盘根兜底会污染范围
            if (raw.Length == 2 && char.IsLetter(raw[0]) && raw[1] == ':')
                return;
            // 纯数字记号(head -c 16、sleep 5 的选项值):几乎总不是文件名
            if (raw.All(char.IsDigit))
                return;
            if (ShellCommandTables.IsUri(raw) || ShellCommandTables.IsNullDevice(raw))
                return;

            // key=value 形式(-F file=@x 的值、curl 的 --data key=v):
            // 后半能解析为真实路径则提取,否则整体记为不可静态判定(从宽)。
            // 仅 Uncertain 命令做:NoEffect 命令(export X=/path 之类)的值不会触发文件访问
            if (cls is CommandClass.Uncertain && raw.Contains('='))
            {
                var after = raw[(raw.IndexOf('=') + 1)..];
                if (after.StartsWith('@'))
                    after = after[1..];
                if (after.Length <= 1 || ShellCommandTables.IsUri(after) || ShellCommandTables.IsNullDevice(after))
                    return; // 空/裸斜杠/http://... :不是文件系统路径
                if (PathResolver.Resolve(cwdAt, after) is { } real && seen.Add(real))
                {
                    result.Paths.Add(real);
                    return;
                }
                if (!seen.Contains(raw))
                {
                    seen.Add(raw);
                    result.UncertainPaths.Add(raw);
                }
                return;
            }

            // curl 的 @file 上传/读取前缀
            if (raw.StartsWith('@'))
                raw = raw[1..];
            if (raw.Length == 0)
                return;

            // 解析为绝对路径;seen(忽略大小写)保证同一路径只出现一次
            var full = PathResolver.Resolve(cwdAt, raw);
            if (full is not null && seen.Add(full))
                result.Paths.Add(full);
        }

        foreach (var seg in segments)
        {
            var intent = result.Commands.FirstOrDefault(c => ReferenceEquals(c.Tokens, seg.Tokens));
            var cwd = intent?.WorkingDirectoryAt ?? workingDirectory;
            var cls = intent?.Class;
            var cmdName = PlainCommandName(seg.Tokens.Name);
            var isCdCmd = ShellCommandTables.Cd.Contains(cmdName);
            // 模式类命令(grep/sed/awk/findstr/sls)首个非选项参数是模式/脚本而非文件
            // (find 首参是待搜索目录,不在此列)
            var skipPatternFirst = ShellCommandTables.ProgramFirstArg.Contains(cmdName) && cmdName != "find";
            var patternSkipped = false;
            // find 只有首个非选项参数(待搜索路径)是路径;其后 -name/-path 等的操作数都是匹配模式
            var findPathTaken = false;
            foreach (var r in seg.Tokens.Redirects)
                Add(r.Target, cwd, CommandClass.Write);
            foreach (var arg in seg.Tokens.Args)
            {
                // 选项记号(-x/--xxx 与 cmd 的 /v /d 等,子类可扩展):值由选项消费,不是独立路径
                if (IsOptionLike(arg))
                    continue;
                if (cmdName == "find")
                {
                    if (findPathTaken)
                        continue; // 表达式操作数(-name 的值等)是模式,不是路径
                    findPathTaken = true;
                }
                // Windows 风格短选项(/i 等,findstr):不是路径,也不消耗"首参跳过"资格
                if (arg.StartsWith('/') && arg.Length <= 3 && skipPatternFirst && !patternSkipped)
                    continue;
                if (skipPatternFirst && !patternSkipped)
                {
                    patternSkipped = true;
                    continue;
                }
                // cd/pushd 的开关(/d、-D):不是路径
                if (isCdCmd && arg.Length <= 2 && arg[0] is '/' or '-')
                    continue;
                // 纯变量/命令引用($HOME/$(x)/${x}):展开结果不可静态定值,已在 Uncertain 记号中
                if (VarRefRegex.IsMatch(arg))
                    continue;
                // 变量+后缀($B/api/health):同样不可静态定值,不拼幻影路径
                if (arg.Contains('$'))
                    continue;
                // key=value 记号(export X=/p):赋值值不触发文件访问
                if (cls is CommandClass.NoEffect && arg.Contains('='))
                    continue;
                // 代码/多词串(python -c "import ...; r.post(...)"):不是路径
                if (cls is CommandClass.Uncertain or CommandClass.NoEffect
                    && (arg.Contains(' ') || arg.Contains('(') || arg.Contains(')')
                        || arg.Contains(';') || arg.Length > 128))
                    continue;
                // 未知命令的"像路径"门槛:纯字母/纯数字/无分隔符无扩展名的参数不是路径
                // (curl 的 POST、ping 的 IP 之类),避免把命令参数当文件追问
                if (cls is CommandClass.Uncertain or CommandClass.NoEffect
                    && !ShellCommandTables.LooksLikePath(arg))
                    continue;
                Add(arg, cwd, cls);
            }
        }

        // 空路径兜底用 shell 最终到达的目录(最后一条命令的工作目录)而非会话工作目录:
        // cd 之后运行的命令实际在别处,按会话目录兜底会让已信任的目录匹配不上白名单
        var finalCwd = result.Commands.Count > 0
            ? result.Commands[^1].WorkingDirectoryAt
            : workingDirectory;

        foreach (var raw in result.UncertainPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (result.Paths.Count >= MaxPaths)
                break;
            // 纯变量/命令引用只是"不可判定"记号,不当作路径
            if (VarRefRegex.IsMatch(raw))
                continue;
            Add(raw, finalCwd, CommandClass.Uncertain);
        }

        if (result.Paths.Count == 0)
            // 会话未设工作目录时 finalCwd 可能为空:退回进程目录,GetFullPath("") 会抛异常
            result.Paths.Add(Path.GetFullPath(string.IsNullOrEmpty(finalCwd) ? "." : finalCwd));
        return result;
    }

    /// <summary>
    /// 剥离 here 文档:bash 的 &lt;&lt;DELIM / &lt;&lt;-DELIM / &lt;&lt;&lt; 与 PowerShell here-string(@'...('@)。
    /// 内容是标准输入数据而非命令:剥离正文,避免被误读成命令或路径。
    /// </summary>
    public static string StripHeredocs(string command)
    {
        var hasBash = command.Contains("<<", StringComparison.Ordinal);
        var hasPs = command.Contains("@'", StringComparison.Ordinal) || command.Contains("@\"", StringComparison.Ordinal);
        if (!hasBash && !hasPs)
            return command;

        var lines = command.Split('\n');
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();

            // PowerShell here-string:整行以 @' 或 @" 结尾(可带 $x = 前缀);结束行以 '@ 或 "@ 开头
            if (trimmed.Length >= 2 && trimmed[^2] == '@' && (trimmed[^1] == '\'' || trimmed[^1] == '"'))
            {
                var close = trimmed[^1].ToString() + "@";
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(close, StringComparison.Ordinal))
                    i++;
                i++; // 跳过结束行
                sb.Append(line[..Math.Max(0, line.Length - trimmed.Length)].TrimEnd());
                sb.Append('\n');
                continue;
            }

            var op = IndexOfHeredoc(line);
            if (op is not null)
            {
                var (start, end, hereString) = op.Value;
                if (hereString)
                {
                    // <<< 行内 here-string:整行保留——内容成为命令参数($() 展开判 Uncertain,纯文本是无害读),
                    // 且操作符行上 ; && 之后的命令不被吞掉
                    sb.Append(line).Append('\n');
                    i++;
                    continue;
                }
                // 剥正文但保留操作符行剩余部分(<< 定界符标记本身丢弃):此前整段丢弃,
                // cat <<EOF; rm -rf x 只分析出 cat,分号/&& 之后的命令在只读/读写档被静默放行
                var (delim, tokenEnd) = ReadHeredocDelimiter(line, end);
                var dash = start + 2 < line.Length && line[start + 1] == '<' && line[start + 2] == '-';
                sb.Append((line[..start] + " " + line[tokenEnd..]).TrimEnd());
                sb.Append('\n');
                i++;
                if (delim is not null)
                {
                    while (i < lines.Length)
                    {
                        var b = lines[i].TrimEnd('\r');
                        var t = dash ? b.TrimStart('\t') : b;
                        if (t.Trim() == delim)
                        {
                            i++;
                            break;
                        }
                        i++;
                    }
                }
                continue;
            }

            sb.Append(line).Append('\n');
            i++;
        }
        return sb.ToString();
    }

    /// <summary>行内首个引号外的 here 文档操作符;返回(起始, 结束, 是否&lt;&lt;&lt;)。</summary>
    private static (int start, int end, bool hereString)? IndexOfHeredoc(string line)
    {
        char? quote = null;
        for (var j = 0; j < line.Length; j++)
        {
            var c = line[j];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }
            if (c != '<' || j + 1 >= line.Length || line[j + 1] != '<')
                continue;
            if (j + 2 < line.Length && line[j + 2] == '<')
                return (j, j + 3, true);
            // << / &lt;&lt;- :要求词边界(行首/空白/数字 fd 前缀),避免匹配 2<<1 之外的代码符号
            if (j > 0 && !char.IsWhiteSpace(line[j - 1]) && !char.IsDigit(line[j - 1]))
                continue;
            return (j, j + 2, false);
        }
        return null;
    }

    /// <summary>读 here 文档定界符(可带引号与 &lt;&lt;- 的连字符):返回(定界符, 操作符行上标记的结束下标)。
    /// 结束下标供调用方拼接操作符行剩余部分(定界符之后的 ; &amp;&amp; 等命令仍需进入分析)。</summary>
    private static (string? Delim, int TokenEnd) ReadHeredocDelimiter(string line, int from)
    {
        var j = from;
        if (j < line.Length && line[j] == '-')
            j++; // <<- 的连字符属于操作符
        while (j < line.Length && char.IsWhiteSpace(line[j]))
            j++;
        var openedQuote = false;
        if (j < line.Length && (line[j] == '\'' || line[j] == '"'))
        {
            openedQuote = true;
            j++;
        }
        var w = new System.Text.StringBuilder();
        while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_'))
        {
            w.Append(line[j]);
            j++;
        }
        if (openedQuote && j < line.Length && (line[j] == '\'' || line[j] == '"'))
            j++; // 收尾引号属于定界符标记
        return (w.Length > 0 ? w.ToString() : null, j);
    }

    /// <summary>按命令词+参数定类:在词表基础上特判 find——无写动作标志时按只读处理
    /// (找截图/找文件是高频只读场景;带 -delete/-exec 的仍保持保守)。</summary>
    protected static CommandClass ClassifyCommand(CommandTokens tokens)
    {
        var cls = ClassifyCommand(tokens.Name);
        if (cls == CommandClass.Uncertain
            && PlainCommandName(tokens.Name) == "find"
            && ShellCommandTables.FindIsReadOnly(tokens.Args))
            cls = CommandClass.Read;
        return cls;
    }

    protected static CommandClass ClassifyCommand(string name)
    {
        if (name.Length == 0)
            return CommandClass.Uncertain;
        var plain = PlainCommandName(name);
        if (ShellCommandTables.Cd.Contains(plain))
            return CommandClass.NoEffect;
        if (ShellCommandTables.NoEffect.Contains(plain))
            return CommandClass.NoEffect;
        if (ShellCommandTables.Write.Contains(plain))
            return CommandClass.Write;
        if (ShellCommandTables.Read.Contains(plain))
            return CommandClass.Read;
        if (plain.Equals("git", StringComparison.OrdinalIgnoreCase))
            return CommandClass.Uncertain; // 子命令面太宽(push/fm 可写),交宿主裁决
        return CommandClass.Uncertain;
    }

    /// <summary>命令词去扩展名(name.exe → name)。</summary>
    protected static string PlainCommandName(string token)
    {
        var lower = token.ToLowerInvariant();
        foreach (var ext in new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1" })
            if (lower.EndsWith(ext))
                return token[..^ext.Length];
        return token;
    }

    /// <summary>sort 的输出选项(-o X / -oX / --output=X;cmd 的 /O 同义):值是写目标。
    /// sort 在 Read 词表,漏看输出选项会把工作目录外的写目标按 Read 级静默放行(sort -o 挖洞)。</summary>
    protected static bool SortWritesToOutput(string name, IEnumerable<string> args, bool windowsSwitch)
    {
        if (!name.Equals("sort", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var a in args)
        {
            var lower = a.ToLowerInvariant();
            if (windowsSwitch)
            {
                if (lower == "/o" || lower.StartsWith("/o"))
                    return true;
            }
            else if (lower is "-o" or "--output" || lower.StartsWith("-o") && lower.Length > 2 || lower.StartsWith("--output="))
                return true;
        }
        return false;
    }

    /// <summary>选项形参:不是路径(bash/通用:- 开头的开关);cmd 的 Windows 风格开关(/v /d)由子类扩展。</summary>
    protected virtual bool IsOptionLike(string arg) => arg.StartsWith('-');

    /// <summary>工作目录所在盘的盘根(裸根路径的落点);解析失败返回 null。</summary>
    private static string? DriveRootOf(string cwd)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(string.IsNullOrEmpty(cwd) ? "." : cwd));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or System.Security.SecurityException or System.IO.PathTooLongException)
        {
            return null;
        }
    }
}
