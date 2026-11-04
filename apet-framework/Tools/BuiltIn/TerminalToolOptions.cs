namespace APet.Framework.Tools.BuiltIn;

/// <summary>终端工具配置。修改 Shell 后,工具定义会即时反映当前使用的终端。</summary>
public sealed class TerminalToolOptions
{
    /// <summary>
    /// 终端程序:"powershell"、"pwsh"、"cmd"、"bash"、"sh" 或完整路径。
    /// 为空时使用系统当前默认:Windows 取 PowerShell(见 TerminalTool.ResolveShell),类 Unix 取 $SHELL,缺省 /bin/sh。
    /// </summary>
    public string? Shell { get; set; }

    /// <summary>工作目录,为空时使用进程当前目录。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>命令超时秒数,超时后终止整个进程树。单次调用可用 timeout_seconds 参数放宽。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>timeout_seconds 参数钳制:下限 10 防无意义的秒级超时误杀,上限 600——
    /// 更长的任务应走 bg_run(后台运行),不是拉长前台阻塞。</summary>
    public static int ClampTimeout(int requested) => Math.Clamp(requested, 10, 600);

    /// <summary>stdout/stderr 各自保留的最大字符数,超出截断。</summary>
    public int MaxOutputChars { get; set; } = 20_000;

    /// <summary>子进程输出编码。为空时按输出内容自动判定(BOM → UTF-16 NUL 分布 → UTF-8 合法性 → 系统代码页,见 OutputTextDecoder);显式指定可跳过探测。</summary>
    public System.Text.Encoding? OutputEncoding { get; set; }
}
