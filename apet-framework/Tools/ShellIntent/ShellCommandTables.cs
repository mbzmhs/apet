namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// 各 shell 的命令词表(静态影响分级用)。同名命令在不同 shell 语义不同,必须分表:
/// bash 的 rm 是删除,PowerShell 的 rm 是 Remove-Item,cmd 的 del 才是删除。
/// </summary>
public static class ShellCommandTables
{
    /// <summary>无文件系统影响的命令(输出信息/查询;一次性 shell 进程无持久副作用)。
    /// 注意:会执行后随命令的包装器(env/timeout/nohup 族)不算"无影响"——它们执行任意命令,
    /// 误入此表等于任意命令免鉴权;查询型的常见用法(reg query/net user 无参)多问一次即可。</summary>
    public static readonly HashSet<string> NoEffect = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "time", "echo", "ver", "whoami", "hostname", "uname", "pwd", "id",
        "cls", "clear", "sleep", "exit", "set", "printenv",
        "true", "false", "ping", "ipconfig", "ifconfig", "netstat", "path",
        "title", "color", "vol", "systeminfo",
        "seq", "printf", "column", "nproc", "uptime", "free", "ps", "tasklist",
        "last", "lastlog", "history", "groups", "logname", "tty", "arch", "getconf",
        "locale", "who",
        "export", "unset", "declare", "local", "readonly", "alias", "unalias", "shift", "hash",
        "get-location", "gl", "get-hostname", "write-host", "write-output", "w",
        // gc 是 Get-Content 别名(读文件),绝不可入此表——NoEffect 优先于 Read 判类,进了就是任意读免鉴权;
        // Get-Command 的别名是 gcm(此处本意)
        "get-date", "format-list", "fl", "measure-object", "help", "get-command", "gcm",
    };

    /// <summary>只读命令(只读取文件/目录/网络状态,无写入通道)。find 不在此列:-delete/-exec 可删改。</summary>
    public static readonly HashSet<string> Read = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "gci", "get-childitem", "cat", "type", "gc", "get-content",
        "head", "tail", "wc", "stat", "du", "df", "grep", "egrep", "fgrep", "findstr",
        "fc", "comp", "diff", "test-path", "sort", "less", "more", "tree", "where", "which",
        "netstat", "arp", "route", "nslookup", "get-item", "gi", "get-filehash",
        "cut", "uniq", "tr", "paste", "comm", "file", "strings",
        "md5sum", "sha1sum", "sha256sum", "sha512sum", "cksum",
        "basename", "dirname", "realpath", "readlink",
        "select-string", "sls", "compare-object", "get-itemproperty",
    };

    /// <summary>写文件/改变目录结构的命令(参数按路径提取)。net/reg 不在此列:它们的参数是
    /// 服务名/注册表键/用户名等非文件系统对象,按路径提取会落成"工作目录内的文件"被静默放行——
    /// 系统级修改交宿主裁决(Uncertain)。</summary>
    public static readonly HashSet<string> Write = new(StringComparer.OrdinalIgnoreCase)
    {
        "cp", "copy", "cpi", "copy-item", "mv", "move", "mi", "move-item",
        "rm", "del", "erase", "ri", "remove-item", "touch", "mkdir", "md",
        "rmdir", "rd", "set-content", "add-content", "ac", "new-item", "ni",
        "out-file", "tee", "tee-object", "export-csv", "export-clixml", "import-csv",
        "rename-item", "ren", "attrib", "vim", "vi", "nano", "link", "mklink",
        "unzip", "tar", "zip", "robocopy", "xcopy",
    };

    /// <summary>cd 及目录切换命令(改变虚拟工作目录;参数是"要去的地方",不是数据路径)。</summary>
    public static readonly HashSet<string> Cd = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "pushd", "popd", "set-location", "sl",
        "push-location", "pop-location", "get-location",
    };

    /// <summary>第一个非选项参数是脚本/模式而非文件路径的命令(跳过首参后再提路径)。</summary>
    public static readonly HashSet<string> ProgramFirstArg = new(StringComparer.OrdinalIgnoreCase)
    {
        "sed", "awk", "grep", "egrep", "fgrep", "findstr", "find", "select-string", "sls",
    };

    /// <summary>值是路径的 PowerShell 命名参数(cmdlet 与 -Path x / -Path:x 形式)。</summary>
    public static readonly HashSet<string> PathParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "literalpath", "filepath", "pspath", "file", "fileinfo",
    };

    /// <summary>PowerShell 取值型非路径参数:紧随的字符串元素是参数值,不是文件系统路径
    /// (-ErrorAction SilentlyContinue)。开关参数(-Recurse/-Force)与未知名不在其中:
    /// 开关后是位置参数,吞值曾把 Remove-Item -Recurse -Force x 的目标整个吞掉。</summary>
    public static readonly HashSet<string> PsValueParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "erroraction", "warningaction", "informationaction",
        "errorvariable", "warningvariable", "informationvariable",
        "outvariable", "outbuffer", "pipelinevariable",
        "encoding", "credential", "width", "delimiter", "itemtype", "value",
        "totalcount", "head", "tail", "first", "skip", "depth",
    };

    /// <summary>git 只读子命令(不改变仓库状态)。</summary>
    public static readonly HashSet<string> GitReadOnlySubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "blame", "shortlog", "ls-files", "describe",
    };

    /// <summary>
    /// 通用路径判定:已存在,或"含分隔符/盘符且无空格",或带常见文件扩展名。
    /// 对未知命令的非选项参数使用——宁可多报(触发询问),不静默漏报。
    /// </summary>
    public static bool LooksLikePath(string token)
    {
        if (token.StartsWith('~'))
            return true; // 用户主目录(或其中子路径):真实目录
        if (token.Length < 2)
            return false;
        if (token.All(char.IsDigit))
            return false;
        if (IsUri(token))
            return false;
        try
        {
            var full = PathResolver.Resolve("", token);
            if (full is not null && (File.Exists(full) || Directory.Exists(full)))
                return true;
        }
        catch
        {
            // 非法路径:落到形态启发式
        }
        if (token.Contains(' '))
            return false;
        if (token.Contains('\\') || token.Contains('/') || token.Contains(':'))
            return true;
        var dot = token.LastIndexOf('.');
        return dot > 0 && dot < token.Length - 1
            && token.Length - dot - 1 is >= 2 and <= 5;
    }

    /// <summary>是否为网络地址(scheme:// 形式);盘符路径(C:\)无 // 不会误判。</summary>
    public static bool IsUri(string token)
    {
        var colon = token.IndexOf(':');
        if (colon < 1 || colon + 2 >= token.Length || token[colon + 1] != '/' || token[colon + 2] != '/')
            return false;
        if (!char.IsLetter(token[0]))
            return false;
        for (var j = 1; j < colon; j++)
            if (!char.IsLetterOrDigit(token[j]) && token[j] is not '+' or '-' or '.')
                return false;
        return true;
    }

    /// <summary>GNU find 的写动作标志:出现任一则命令可增删改文件系统(如 -exec rm {} \;)。</summary>
    public static readonly HashSet<string> FindWriteActions =
        new(StringComparer.OrdinalIgnoreCase)
        { "delete", "exec", "execdir", "ok", "okdir", "fprint", "fprintf", "cpio" };

    /// <summary>find 无写动作标志时是纯只读遍历(找文件/截图等高频场景)。</summary>
    public static bool FindIsReadOnly(IReadOnlyList<string> args)
    {
        foreach (var a in args)
            if (a.Length > 1 && a[0] == '-' && FindWriteActions.Contains(a[1..]))
                return false;
        return true;
    }

    /// <summary>空设备:2>/dev/null 之类,不是真实文件系统路径。</summary>
    public static bool IsNullDevice(string token) =>
        token is "/dev/null" or "/dev/zero" or "nul" or "NUL";
}
