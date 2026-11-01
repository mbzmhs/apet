namespace APet.Framework.Tools.ShellIntent;

/// <summary>终端 shell 类型:决定用哪个仿真解析器推演命令意图。</summary>
public enum ShellKind
{
    Bash,
    Cmd,
    PowerShell,
}

/// <summary>把宿主配置的 shell 路径/名称归一为 ShellKind(与 TerminalTool.ResolveShell 的识别规则一致)。</summary>
public static class ShellKindResolver
{
    public static ShellKind Resolve(string? shell)
    {
        if (string.IsNullOrWhiteSpace(shell))
            return OperatingSystem.IsWindows() ? ShellKind.PowerShell : ShellKind.Bash;
        var name = Path.GetFileName(shell.Trim()).ToLowerInvariant();
        return name switch
        {
            "powershell.exe" or "powershell" or "pwsh.exe" or "pwsh" => ShellKind.PowerShell,
            "cmd.exe" or "cmd" => ShellKind.Cmd,
            _ => ShellKind.Bash,
        };
    }
}
