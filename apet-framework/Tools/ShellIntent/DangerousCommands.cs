namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// 灾难命令清单:递归/批量删除、格式化、强推等难以回退的操作,命中即要求逐次确认。
/// 只服务"加确认"方向——误报代价是一次弹窗,漏报代价是灾难,故规则从宽:
/// 删除类命令带未知旗标也按命中处理。解释器(bash -c、powershell -c 等)不展开,
/// 其效果本来就判为未知,由未知兜底弹窗。
/// </summary>
public static class DangerousCommands
{
    /// <summary>rm 单字符旗标里公认无害的字符;簇里出现清单外字符按危险处理(宁可多问)。</summary>
    private const string HarmlessRmFlags = "fivdnqh?";

    /// <summary>删除类命令的已知无害长参数名(PowerShell 风格,参数记号已由 PS 分析器记回
    /// Args):不触发"未知旗标从宽"。recurse/recursive 不在此列——命中即按递归判危险。</summary>
    private static readonly HashSet<string> HarmlessDeleteParams = new(StringComparer.Ordinal)
    {
        "path", "literalpath", "filter", "include", "exclude", "credential", "force",
        "whatif", "confirm", "verbose", "debug", "passthru",
        "erroraction", "warningaction", "informationaction",
        "errorvariable", "warningvariable", "informationvariable",
        "outvariable", "outbuffer", "pipelinevariable",
    };

    public static bool IsInvolved(IntentResult intent)
    {
        foreach (var command in intent.Commands)
        {
            if (Match(command.Tokens))
                return true;
        }
        return false;
    }

    private static bool Match(CommandTokens t)
    {
        var name = BaseName(t.Name);
        switch (name)
        {
            // rm 兼顾 POSIX 与 PowerShell 别名(Remove-Item):递归即危险
            case "rm":
            case "ri":
            case "remove-item":
                return IsRecursiveDelete(t, unknownFlagIsDangerous: true);
            case "del":
            case "erase":
            case "rd":
            case "rmdir":
                return IsRecursiveDelete(t, unknownFlagIsDangerous: false);
            case "robocopy":
                return t.Args.Any(a => SameText(a, "/mir") || SameText(a, "/purge"));
            case "format":
            case "diskpart":
            case "dd":
            case "format-volume": // PowerShell 磁盘格式化(Format-Volume)
            case "clear-disk":    // PowerShell 磁盘清空(Clear-Disk)
                return true;
            case "git":
                return MatchGit(t.Args);
            default:
                return name.StartsWith("mkfs.", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>删除类命令:递归旗标、批量(目标 ≥2 或带通配)即危险。</summary>
    private static bool IsRecursiveDelete(CommandTokens t, bool unknownFlagIsDangerous)
    {
        var operands = 0;
        foreach (var arg in t.Args)
        {
            if (arg.Length == 0)
                continue;
            if (arg[0] is '-' or '/')
            {
                var body = arg[1..];
                if (body.Length == 0 || body == "-")
                    continue; // "-" 单独出现是 stdin 约定,"--" 之后全是目标
                if (arg.StartsWith("--"))
                {
                    var longName = body.Split('=')[0];
                    if (longName.Contains("recursive", StringComparison.OrdinalIgnoreCase)
                        || longName.Contains("recurse", StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                var cluster = body.ToLowerInvariant();
                // PowerShell 长参数名(-Recurse/-Path,长度 >3)按名判定,不做逐字符扫描——
                // -LiteralPath 里的 r 不是递归旗标
                if (cluster.Length > 3)
                {
                    if (cluster.Contains("recurse") || cluster.Contains("recursive"))
                        return true;
                    if (unknownFlagIsDangerous && !HarmlessDeleteParams.Contains(cluster))
                        return true; // 未知名:参数含义无法预判,从宽
                    continue;
                }
                if (cluster.Any(c => c is 'r' or 's')) // r/R=递归(POSIX/PS 缩写), s=cmd /s 子目录
                    return true;
                if (unknownFlagIsDangerous && cluster.Any(c => !HarmlessRmFlags.Contains(c)))
                    return true; // rm/Remove-Item 的陌生旗标:参数含义无法预判,从宽
                continue;
            }
            // 删除类命令的 "." / ".." 目标:语法上即"清空当前目录"(cmd 的 del . 形态)
            if (arg is "." or "..")
                return true;
            operands++;
            if (operands >= 2 || arg.Contains('*') || arg.Contains('?'))
                return true; // 批量目标:语法上即批量,不展开求证
        }
        return false;
    }

    private static bool MatchGit(List<string> args)
    {
        var sub = GitSubcommandArgs(args);
        if (sub.Count == 0)
            return false;
        switch (sub[0])
        {
            case "reset":
                return sub.Any(a => SameText(a, "--hard"));
            case "clean":
                return HasForce(sub);
            case "push":
                return HasForce(sub);
            case "branch":
                // -D 即强删;--delete/-d 配 -f 同义(-f 把"未合并拒删"的保险关掉);--delete 单独是安全形态
                var branchDelete = sub.Any(a => SameText(a, "-D") || SameText(a, "--delete") || SameText(a, "-d"));
                return branchDelete && (sub.Any(a => SameText(a, "-D")) || HasForce(sub));
            default:
                return false;
        }
    }

    /// <summary>跳过 git 全局选项(-c/-C 带取值)取子命令及其参数。</summary>
    private static List<string> GitSubcommandArgs(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Length == 0 || arg[0] != '-')
                return args.Skip(i).ToList();
            if (arg is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix")
                i++; // 取值型全局选项:连值一起跳过
        }
        return [];
    }

    /// <summary>强制旗标识别(簇旗标从宽):-f/-fd 等含 f 的短簇、--force 精确名、+refspec
    /// (git push origin +main 强推单个引用);--force-with-lease 是带保险的强推,不视为灾难。</summary>
    private static bool HasForce(List<string> args) =>
        args.Any(a =>
        {
            if (a.StartsWith('+'))
                return true; // +refspec:强推该引用
            if (a.StartsWith("--"))
                return SameText(a, "--force"); // 精确名;force-with-lease 等不命中
            var body = a.ToLowerInvariant().TrimStart('-');
            return body is "f" or "force"
                || (a.StartsWith('-') && body.Contains('f') && body.All(c => char.IsLetter(c))); // -fd 等短簇
        });

    private static bool SameText(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>去路径与可执行扩展名(分析器产出多为裸名,这里兜底完整路径形式)。</summary>
    private static string BaseName(string name)
    {
        var normalized = name.Replace('\\', '/');
        var baseName = normalized[(normalized.LastIndexOf('/') + 1)..];
        foreach (var ext in new[] { ".exe", ".com", ".cmd", ".bat", ".ps1" })
        {
            if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return baseName[..^ext.Length];
        }
        return baseName.ToLowerInvariant();
    }
}
