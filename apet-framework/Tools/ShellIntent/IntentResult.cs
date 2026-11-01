using APet.Framework.Security;

namespace APet.Framework.Tools.ShellIntent;

/// <summary>一条(管道段中的)命令经仿真解析后的形态。</summary>
public sealed class CommandTokens
{
    public string Name = "";
    public List<string> Args = [];
    public List<RedirectIntent> Redirects = [];

    public bool IsPipeHead { get; set; }
}

/// <summary>重定向意图:对哪个文件描述符、以何种方式(写/追加/读)作用于哪个文件。</summary>
public sealed class RedirectIntent
{
    public int Fd { get; init; }
    public bool Append { get; init; }
    public bool Input { get; init; }
    public string Target { get; init; } = "";

    public FileAccessKind Access => Input ? FileAccessKind.Read : FileAccessKind.Write;
}

/// <summary>单条命令的静态影响类别(仿真解析得出)。</summary>
public enum CommandClass
{
    /// <summary>无法静态判定(未知命令/内嵌子命令/未定变量…)——最保守。</summary>
    Uncertain,
    /// <summary>无文件系统影响(ping/ipconfig/echo/date 等)。</summary>
    NoEffect,
    /// <summary>只读(ls/cat/type 等)。</summary>
    Read,
    /// <summary>写文件/改变目录内容(rm/copy/重定向等)。</summary>
    Write,
}

/// <summary>一条命令的完整意图:命令词、参数、重定向、生效时的(虚拟)工作目录。</summary>
public sealed class CommandIntent
{
    public CommandTokens Tokens = new();
    public string WorkingDirectoryAt = "";
    public CommandClass Class = CommandClass.Uncertain;
}

/// <summary>整条命令行的仿真解析结果:命令序列意图 + 受影响路径 + 最强访问级别。</summary>
public sealed class IntentResult
{
    public List<CommandIntent> Commands = [];

    /// <summary>无法静态定值但可能触碰路径的记号(如 $VAR 文件、未展开的 %VAR%)。</summary>
    public List<string> UncertainPaths = [];

    public bool HasUncertain { get; set; }

    /// <summary>全部路径(命令参数 + 重定向目标 + 不确定记号)。</summary>
    public List<string> Paths { get; } = [];

    /// <summary>最强访问级别:任一 Uncertain → Unknown;任一 Write → Write;否则有 Read → Read;全 NoEffect → None。</summary>
    public FileAccessKind StrongestAccess
    {
        get
        {
            if (HasUncertain)
                return FileAccessKind.Unknown;
            var hasWrite = Commands.Any(c => c.Class == CommandClass.Write);
            if (hasWrite)
                return FileAccessKind.Write;
            var hasRead = Commands.Any(c => c.Class == CommandClass.Read);
            return hasRead ? FileAccessKind.Read : FileAccessKind.None;
        }
    }
}
