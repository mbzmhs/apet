namespace APet.Framework.Tools.ShellIntent;

/// <summary>
/// 各 shell 仿真解析器的统一入口。"仿真"=不执行任何实际操作,只按各 shell 的语法
/// (分词/引号/转义/变量/重定向/命令链)推演命令的真实意图:cd 改变虚拟工作目录、
/// 重定向=文件写、命令词表定读写——得到的目录与操作即命令真实会触碰的范围。
/// </summary>
public static class IntentAnalyze
{
    /// <summary>
    /// PowerShell 精确解析器(宿主注入:基于 System.Management.Automation 的 AST)。
    /// 未注入时 PowerShell 命令走 <see cref="GenericIntentAnalyzer"/> 保守回退。
    /// </summary>
    public static IIntentAnalyzer? PowerShellAnalyzer { get; set; }

    public static IntentResult Analyze(string command, string workingDirectory, ShellKind shell)
    {
        var analyzer = shell switch
        {
            ShellKind.Cmd => new CmdIntentAnalyzer(),
            ShellKind.PowerShell => PowerShellAnalyzer ?? new GenericIntentAnalyzer(),
            _ => new BashIntentAnalyzer(),
        };
        return analyzer.Analyze(command ?? "", workingDirectory);
    }
}
