using System.Diagnostics;
using System.Text;
using System.Text.Json;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools.ShellIntent;

namespace APet.Framework.Tools.BuiltIn;

/// <summary>
/// 执行终端命令。使用的终端可配置(Shell 属性),
/// 并体现在工具定义的描述中,宿主修改后下一次导出定义即生效。
/// </summary>
public sealed class TerminalTool : BuiltinToolBase
{
    /// <summary>进程退出后等待输出管道排空的宽限:命令留下的后台子进程可能仍持有管道(永远等不到 EOF),
    /// 超时即放弃——否则工具永不返回。</summary>
    private const int DrainGraceMs = 1500;

    private readonly TerminalToolOptions _options;

    public TerminalTool(TerminalToolOptions? options = null)
    {
        _options = options ?? new TerminalToolOptions();
    }

    /// <summary>当前配置(宿主可修改 Shell/OutputEncoding 后热生效,定义描述随之更新)。</summary>
    public TerminalToolOptions Options => _options;

    public override ToolDefinition Definition
    {
        get
        {
            var shell = ResolveShell();
            return ToolDefinition.Create(
                "bash",
                $"Execute a single command in {shell.DisplayName} and return JSON (exit_code/stdout/stderr/timed_out)."
                + $" Timeout: {_options.TimeoutSeconds} seconds. Current terminal: {shell.DisplayName}.",
                """
                {
                  "type": "object",
                  "properties": {
                    "command": { "type": "string", "description": "The command to execute" }
                  },
                  "required": ["command"]
                }
                """,
                displayName: "运行终端");
        }
    }

    /// <summary>
    /// 终端命令的影响分析:按当前终端类型(bash/cmd/powershell)用对应仿真解析器推演命令
    /// 真实意图(cd 的目录推演、重定向=文件写、命令词表定读写),得到命令真实触碰的路径与
    /// 读写级别。可静态定级时按级别放行(无影响 date/ping 等 → None;只读 ls/cat 等 → Read),
    /// 不可静态判定(未知命令/子命令/未定变量)→ Unknown 交宿主回调。
    /// 命令未显式引用任何路径时,把执行工作目录(CWD)计入影响范围:
    /// shell 命令总是以当前目录为基准隐式操作(相对路径/重定向等),权限应受当前目录级别
    /// 束缚——工作目录为完全访问时可自动放行,否则仍交宿主回调,不会越出工作目录。
    /// </summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var command = doc.RootElement.GetProperty("command").GetString() ?? "";
            var workingDirectory = ToolExecutionScope.WorkingDirectory
                ?? _options.WorkingDirectory
                ?? Environment.CurrentDirectory;
            var kind = ShellKindResolver.Resolve(_options.Shell);
            var intent = IntentAnalyze.Analyze(command, workingDirectory, kind);
            return new ToolEffect
            {
                Access = intent.StrongestAccess,
                Paths = intent.Paths,
                Dangerous = DangerousCommands.IsInvolved(intent),
            };
        }
        catch (JsonException)
        {
            return ToolEffect.UnknownEffect;
        }
    }

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var command = doc.RootElement.GetProperty("command").GetString();
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command must not be empty.");

        var shell = ResolveShell();
        var psi = new ProcessStartInfo(shell.FileName)
        {
            WorkingDirectory = ToolExecutionScope.WorkingDirectory
                ?? _options.WorkingDirectory
                ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 不预设输出编码:读原始字节后按内容自动判定(见 OutputTextDecoder),
            // 因命令输出编码因程序而异(bash/构建=UTF-8、wsl=UTF-16、老程序=系统代码页)
        };
        ApplyCommand(psi, shell, command);

        using var process = new Process { StartInfo = psi };
        process.Start();

        // 输出读取:原始字节留档(结束时按内容自动判定编码解码,与既往行为一致),
        // 同时增量解码推给宿主实时显示(见 PumpAsync/LiveStream)。
        // 进程退出后管道可能仍被后台子进程持有(读不到 EOF),pumpCts 用于在排空超时/取消时收摊
        var stdoutRaw = new RawCapture();
        var stderrRaw = new RawCapture();
        var liveSink = ToolExecutionScope.Output;
        var liveStdout = new LiveStream(liveSink, "stdout", _options.MaxOutputChars);
        var liveStderr = new LiveStream(liveSink, "stderr", _options.MaxOutputChars);
        using var pumpCts = new CancellationTokenSource();
        var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, stdoutRaw, liveStdout, pumpCts.Token);
        var stderrTask = PumpAsync(process.StandardError.BaseStream, stderrRaw, liveStderr, pumpCts.Token);

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillTree(process);
            await WaitExitQuietlyAsync(process);
        }
        catch (OperationCanceledException)
        {
            // 整轮取消(用户点停止):终止进程树并原样上抛——取消必须立刻生效,
            // 不能卡在管道读取上(卡住则聊天界面表现为"无法中断")
            KillTree(process);
            pumpCts.Cancel();
            liveStdout.Flush();
            liveStderr.Flush();
            throw;
        }

        // 排空管道:正常退出的命令毫秒级读完;后台子进程持有管道时到点即停,读多少算多少
        var drained = await DrainAsync(stdoutTask, stderrTask, cancellationToken);
        if (!drained)
            pumpCts.Cancel();
        liveStdout.Flush();
        liveStderr.Flush();

        var stderrText = DecodeOutput(stderrRaw.Snapshot());
        if (!drained)
            stderrText = (stderrText + "\n[output drain timed out: a background child process still holds the output pipes; output above may be incomplete]").TrimStart('\n');

        var result = new
        {
            command,
            exit_code = process.HasExited ? process.ExitCode : -1,
            timed_out = timedOut,
            // 输出未完全排空(命令留下了持有管道的后台子进程):模型据此知道上面输出可能不完整
            output_incomplete = !drained,
            stdout = Truncate(DecodeOutput(stdoutRaw.Snapshot())),
            stderr = Truncate(stderrText),
        };
        return JsonSerializer.Serialize(result, ToolJson.Options);
    }

    /// <summary>
    /// 读一条重定向输出:原始字节写入 capture(供结束时统一解码),增量解码文本推给 LiveStream。
    /// 进程被杀/管道被后台子进程持有而不达 EOF 时,保留已读部分即可(取消由 pumpCts 触发)。
    /// </summary>
    private async Task PumpAsync(Stream stream, RawCapture capture, LiveStream live, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var decoder = new IncrementalTextDecoder(_options.OutputEncoding); // 宿主指定了编码就按它;否则自动判定
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                capture.Write(buffer, read);
                live.Push(decoder.Push(buffer, read));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 管道中断/读取终止:已读部分保留
        }
        finally
        {
            live.Push(decoder.Flush());
        }
    }

    /// <summary>进程退出后等两条管道读到 EOF;超时返回 false(管道仍被后台子进程持有),取消则上抛。</summary>
    private static async Task<bool> DrainAsync(Task stdoutTask, Task stderrTask, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(DrainGraceMs), cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>终止进程树;已退出/权限异常都不上抛,失败只意味着残留进程,不该污染工具结果。</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception)
            {
                // 进程已消失或不可终止:交给超时/退出码兜底
            }
        }
    }

    /// <summary>等进程真正退出(限时),使 ExitCode 可读、输出落定;超时也继续,不阻塞结果返回。</summary>
    private static async Task WaitExitQuietlyAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 进程未能退出:结果里 exit_code = -1
        }
    }

    private string Truncate(string text) =>
        text.Length <= _options.MaxOutputChars ? text : text[.._options.MaxOutputChars] + $"\n...[output truncated, {text.Length} chars in total]";

    // ───────── 命令传递方式(各 shell 的引号规则不同,不能一刀切走 ArgumentList) ─────────

    /// <summary>
    /// 关键修复:cmd 不识别 .NET ArgumentList 的 \" 转义(MSVCRT 约定)——含双引号的命令
    /// (如 dir /b "C:\Users\x")会被解析成带反斜杠的坏路径,报「语法不正确/找不到文件」。
    /// 改用 cmd 官方约定:/s /c "整条命令" —— cmd 剥掉首尾引号、内部引号保持原语义。
    /// </summary>
    private static void ApplyCommand(ProcessStartInfo psi, ShellInfo shell, string command)
    {
        if (shell.EncodeCommand)
        {
            // PowerShell 引号规则复杂且各版本不一致,用 -EncodedCommand(base64 UTF-16LE)彻底绕开转义。
            // 外层包装解决 PS5.1 输出重定向下的三个坑:
            // 1) 错误/进度流会被序列化成 CLIXML XML(模型不可读):关 Progress 进度流,
            //    & { } 2>&1 把错误记录以普通文本并入 stdout(命令自身的解析错误无法捕获,仍走 stderr);
            // 2) 2>&1 会把 $? 洗成成功:管道里顺手把 ErrorRecord 记进 $errs 判失败;
            // 3) 退出码:原生命令优先回传 $LASTEXITCODE(0 也精确回传),仅 cmdlet 失败时 exit 1。
            // 关键:错误记录用管道逐条过 ForEach-Object 落地,不用 "$out = & {...}" 收集——
            // 变量收集会把整条命令的输出扣到命令结束后才吐(长命令实时输出全被吞),管道则边跑边出。
            var wrapped = "$ProgressPreference='SilentlyContinue'; "
                + "$errs = [System.Collections.ArrayList]::new(); "
                + "& { " + command + " } 2>&1 | ForEach-Object { "
                + "if ($_ -is [System.Management.Automation.ErrorRecord]) { $null = $errs.Add($_) }; \"$_\" }; "
                + "if ($LASTEXITCODE -is [int]) { exit $LASTEXITCODE } elseif ($errs.Count -gt 0) { exit 1 }";
            foreach (var arg in shell.PrefixArgs)
                psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)));
            return;
        }
        if (shell.ManualQuote)
        {
            psi.Arguments = string.Join(" ", shell.PrefixArgs) + " \"" + command + "\"";
            return;
        }
        // bash/sh:argv 解析与 .NET 的 \" 转义兼容,命令作为单个参数传递
        foreach (var arg in shell.PrefixArgs)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(command);
    }

    /// <summary>
    /// 解码命令输出:宿主显式指定 OutputEncoding 时按之,否则按内容自动判定
    /// (BOM → UTF-16 的 NUL 分布 → UTF-8 合法性 → 系统代码页;详见 OutputTextDecoder)。
    /// 不同程序输出编码天生不同(bash/构建=UTF-8、wsl=UTF-16、老程序=GBK),固定单一编码必乱其一。
    /// </summary>
    private string DecodeOutput(byte[] bytes) =>
        _options.OutputEncoding is { } forced ? forced.GetString(bytes) : OutputTextDecoder.Decode(bytes);

    /// <summary>原始字节留档(结束时按 OutputTextDecoder 统一判定编码解码);与仍在收尾的读取任务共享,读写都走锁。</summary>
    private sealed class RawCapture
    {
        private readonly MemoryStream _buffer = new();
        private readonly object _gate = new();

        public void Write(byte[] bytes, int count)
        {
            lock (_gate)
                _buffer.Write(bytes, 0, count);
        }

        public byte[] Snapshot()
        {
            lock (_gate)
                return _buffer.ToArray();
        }
    }

    /// <summary>
    /// 一条输出流的实时展示通道:攒够一定长度或间隔即推送文本增量(节流,避免高频小包刷屏),
    /// 总量封顶避免刷爆界面与事件流;宿主未接输出出口(ToolExecutionScope.Output 为 null)时不推送。
    /// </summary>
    private sealed class LiveStream
    {
        private const int FlushChars = 512;
        private const int FlushIntervalMs = 120;

        private readonly Action<string, string>? _sink;
        private readonly string _name;
        private readonly int _cap;
        private readonly bool _labelLines;
        private readonly StringBuilder _pending = new();
        private readonly System.Diagnostics.Stopwatch _sinceFlush = System.Diagnostics.Stopwatch.StartNew();
        private int _sent;
        private bool _truncatedNoted;
        private bool _atLineStart = true;

        public LiveStream(Action<string, string>? sink, string name, int cap)
        {
            _sink = sink;
            _name = name;
            _cap = Math.Max(1024, cap);
            _labelLines = name == "stderr"; // stderr 行首加 [stderr] 前缀,与结束后的详情格式一致
        }

        public void Push(string text)
        {
            if (_sink is null || string.IsNullOrEmpty(text))
                return;
            if (_sent >= _cap)
            {
                if (!_truncatedNoted)
                {
                    _truncatedNoted = true;
                    Emit("\n…（实时预览已截断，完整输出见工具结果）\n");
                }
                return;
            }
            _pending.Append(_labelLines ? Label(text) : text);
            if (_pending.Length >= FlushChars || _sinceFlush.ElapsedMilliseconds >= FlushIntervalMs)
                Flush();
        }

        public void Flush()
        {
            if (_pending.Length == 0)
                return;
            var text = _pending.ToString();
            _pending.Clear();
            Emit(text);
        }

        private void Emit(string text)
        {
            if (_sink is null || text.Length == 0)
                return;
            _sent += text.Length;
            _sinceFlush.Restart();
            try
            {
                _sink(_name, text);
            }
            catch (Exception)
            {
                // 展示层故障绝不影响命令执行
            }
        }

        private string Label(string text)
        {
            var sb = new StringBuilder(text.Length + 16);
            var start = 0;
            while (start < text.Length)
            {
                var nl = text.IndexOf('\n', start);
                if (_atLineStart)
                    sb.Append("[stderr] ");
                if (nl < 0)
                {
                    sb.Append(text, start, text.Length - start);
                    _atLineStart = false;
                    break;
                }
                sb.Append(text, start, nl - start + 1);
                start = nl + 1;
                _atLineStart = true;
            }
            return sb.ToString();
        }
    }

    private ShellInfo ResolveShell()
    {
        var shell = _options.Shell?.Trim();
        if (!string.IsNullOrEmpty(shell))
        {
            var name = Path.GetFileName(shell).ToLowerInvariant();
            return name switch
            {
                "powershell.exe" or "powershell" or "pwsh.exe" or "pwsh"
                    => new ShellInfo(shell, ["-NoProfile", "-NonInteractive"], $"PowerShell({shell})") { EncodeCommand = true },
                "cmd.exe" or "cmd"
                    => new ShellInfo(shell, ["/d", "/s", "/c"], $"CMD({shell})") { ManualQuote = true },
                _ => new ShellInfo(shell, ["-c"], shell),
            };
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows 默认走 PowerShell 而非 cmd:cmd 的 start 内建命令把
            // 第一个带引号参数当「窗口标题」,模型写 start "https://…" 只会弹一个空 cmd 窗口;
            // PowerShell 的 start 是 Start-Process 别名,URL/文件直接打开,常见命令无需学任何 shell 陷阱。
            // pwsh(7)非系统自带不作默认;需要 cmd/其他终端时在 Shell 配置里指定。
            return new ShellInfo("powershell.exe", ["-NoProfile", "-NonInteractive"], "PowerShell(系统默认)")
            {
                EncodeCommand = true,
            };
        }

        var sh = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(sh))
            sh = "/bin/sh";
        return new ShellInfo(sh, ["-c"], $"{sh} (system default)");
    }

    private sealed record ShellInfo(string FileName, string[] PrefixArgs, string DisplayName)
    {
        /// <summary>cmd 约定:手工拼 /s /c "整条命令"(剥首尾引号,内部引号原样保留)。</summary>
        public bool ManualQuote { get; init; }

        /// <summary>PowerShell:用 -EncodedCommand 传命令,绕开引号转义。</summary>
        public bool EncodeCommand { get; init; }
    }
}
