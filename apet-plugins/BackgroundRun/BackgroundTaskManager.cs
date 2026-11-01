using System.Diagnostics;
using System.Globalization;
using System.Text;
using APet.Framework.Tools;

namespace APet.Plugins.BackgroundRun;

/// <summary>后台任务状态。</summary>
public enum BackgroundTaskStatus
{
    Running,
    Completed, // 正常退出(exit code 0)
    Failed,    // 非零退出
    Killed,    // 被 bg_kill 或宿主关闭时终止
}

/// <summary>一个后台命令任务:进程句柄 + 输出环形缓冲。</summary>
public sealed class BackgroundTask
{
    public int Id { get; init; }
    public string Command { get; init; } = "";
    /// <summary>任务名称(bg_run 的 title 参数;展示在侧边栏,替代纯命令行的模糊标题)。可为空(未给时界面回退显示命令)。</summary>
    public string Title { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; private set; }
    public BackgroundTaskStatus Status { get; private set; } = BackgroundTaskStatus.Running;
    public int? ExitCode { get; private set; }

    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private long _trimmed; // 已从头裁掉的字符数(绝对偏移 = 缓冲内偏移 + _trimmed)
    private Process? _process;
    private Task[]? _pumps; // 两条输出 pump 任务(stdout/stderr):OnExited 翻终态前须等它们排空管道尾部

    /// <summary>任务结束(退出/被杀)后触发一次;输出行不触发(避免 UI 高频重绘)。</summary>
    public event Action? Exited;

    internal static BackgroundTask Start(int id, string command, string workingDirectory, string title, int maxOutputChars, string? shell)
    {
        var shellName = (shell ?? "").Trim().ToLowerInvariant();
        var isBash = shellName is "bash" or "sh" or "git-bash";
        var isPowershell = shellName is "powershell" or "pwsh";
        var isCmd = !isBash && !isPowershell; // 空/未知 → 沿用历史默认 CMD

        var psi = new ProcessStartInfo
        {
            FileName = isBash ? "bash" : isPowershell ? "powershell" : Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 不预设输出编码:原始字节按内容自动判定(见 OutputTextDecoder),
            // 因命令输出编码因程序而异(bash/构建=UTF-8、wsl=UTF-16、老程序=GBK)
        };
        ApplyShellCommand(psi, isCmd, isPowershell, command);

        var task = new BackgroundTask
        {
            Id = id,
            Command = command,
            Title = title,
            WorkingDirectory = psi.WorkingDirectory,
            StartedAt = DateTime.Now,
        };
        var process = new Process { StartInfo = psi };
        task._process = process;

        process.Exited += (_, _) => task.OnExited();

        process.Start();
        // 原始字节流式读取:预读判定编码后逐行并入环形缓冲(编码因程序而异,见 PumpAsync)
        var pumpOut = PumpAsync(task, process.StandardOutput.BaseStream, maxOutputChars);
        var pumpErr = PumpAsync(task, process.StandardError.BaseStream, maxOutputChars);
        task._pumps = [pumpOut, pumpErr];
        // 启用退出事件放在 pump 就绪之后:OnExited 翻终态前要等 pump 排空,先登记 pump 消除
        // "Exited 先触发而 pump 列表未就绪"的竞态;进程若恰在启用前退出,句柄已置位,Exited 仍会补发
        process.EnableRaisingEvents = true;
        return task;
    }

    /// <summary>
    /// 按 shell 组装命令行(cmd 用 Arguments、其余用 ArgumentList,二者互斥)。
    /// PowerShell 与框架 TerminalTool 一致走 -EncodedCommand 绕开引号,但后台是长任务需实时看输出,
    /// 故不做 $out 收集,直接流式管道(& {cmd} 2>&1 | ForEach),命令边跑边出、退出码经 $LASTEXITCODE 透传。
    /// </summary>
    private static void ApplyShellCommand(ProcessStartInfo psi, bool isCmd, bool isPowershell, string command)
    {
        if (isCmd)
        {
            // 与框架 TerminalTool 的 cmd 约定一致:/d /s /c "整条命令"(剥首尾引号,内部引号原样保留)
            psi.Arguments = "/d /s /c \"" + command + "\"";
            return;
        }
        if (isPowershell)
        {
            string wrapped =
                "$ProgressPreference='SilentlyContinue'; & { " + command +
                " } 2>&1 | ForEach-Object { \"$_\" }; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }";
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)));
            return;
        }
        // bash/sh:argv 解析与 .NET 的 \" 转义兼容,命令作为单个 -c 参数
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
    }

    private void Append(string? line, int maxOutputChars)
    {
        if (line is null)
            return;
        lock (_gate)
        {
            _output.AppendLine(line);
            TrimIfNeeded(maxOutputChars);
        }
    }

    /// <summary>超限裁掉前半:输出是流式的,保留最新的即可。</summary>
    private void TrimIfNeeded(int maxOutputChars)
    {
        if (_output.Length > maxOutputChars)
        {
            var drop = _output.Length / 2;
            _trimmed += drop;
            _output.Remove(0, drop);
        }
    }

    /// <summary>
    /// 流式读取一条重定向输出:增量解码交给框架 IncrementalTextDecoder(与运行终端工具同一实现:
    /// 纯 ASCII 整块即时出,出现歧义字节后攒样本判定编码,跨块多字节字符不劈坏),
    /// 这里只负责把解出的文本按行入缓冲。
    /// 此前把输出攒到"出现非 ASCII 字节或攒满 256KB"才判定并入缓冲:纯英文输出(构建日志/进度)
    /// 凑不齐非 ASCII 字节,直到进程退出才一次性可见(侧边栏"输出直到结束才完整"的根因)。
    /// </summary>
    private static async Task PumpAsync(BackgroundTask task, Stream raw, int maxOutputChars)
    {
        var decoder = new IncrementalTextDecoder();
        var line = new StringBuilder(); // 未成行的余量(行尾到达才入缓冲)
        void Feed(string text)
        {
            if (text.Length == 0)
                return;
            var start = 0;
            while (true)
            {
                var nl = text.IndexOf('\n', start);
                if (nl < 0)
                {
                    line.Append(text, start, text.Length - start);
                    return;
                }
                line.Append(text, start, nl - start);
                var full = line.ToString();
                line.Clear();
                task.Append(full.TrimEnd('\r'), maxOutputChars);
                start = nl + 1;
            }
        }
        try
        {
            var buf = new byte[4096];
            int n;
            while ((n = await raw.ReadAsync(buf.AsMemory())) > 0)
                Feed(decoder.Push(buf, n));
        }
        catch
        {
            // 进程被杀/崩溃致管道中断:保留已读到的输出即可
        }
        Feed(decoder.Flush());
        if (line.Length > 0)
            task.Append(line.ToString(), maxOutputChars); // 末尾未换行的残余行
    }

    /// <summary>翻终态前等待输出 pump 排空的上限:进程退出即关闭管道写端,pump 读到 EOF 毫秒级结束;
    /// 上限只为兜底病态场景(分离的子进程继承写端,EOF 迟迟不来),不让任务状态无限停留 Running。</summary>
    private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 进程退出:先等两条输出 pump 读完管道尾部再翻终态(进程退出 ≠ 管道排空,此前把 completed
    /// 翻在尾部输出落进缓冲之前,bg_output 会读不到最后几行)。pump 只等流 EOF、不依赖本方法,
    /// 此处等待无死锁环。幂等:状态只翻一次,Kill/重复事件再进来直接跳过。
    /// </summary>
    private async void OnExited()
    {
        try
        {
            bool exited;
            try { exited = _process?.HasExited == true; }
            catch { exited = false; } // 句柄异常按未退出处理:不翻状态,交给真正的 Exited 事件收敛
            if (!exited)
                return;

            var pumps = _pumps;
            if (pumps is { Length: > 0 })
            {
                try
                {
                    await Task.WhenAll(pumps).WaitAsync(PumpDrainTimeout).ConfigureAwait(false);
                }
                catch
                {
                    // 超时或 pump 异常:照常收敛(已读到的输出都在缓冲里,尾部缺失好过状态永久卡 Running)
                }
            }

            bool raise = false;
            lock (_gate)
            {
                if (Status == BackgroundTaskStatus.Running)
                {
                    EndedAt = DateTime.Now;
                    try
                    {
                        ExitCode = _process?.HasExited == true ? _process.ExitCode : null;
                        Status = ExitCode is 0 ? BackgroundTaskStatus.Completed : BackgroundTaskStatus.Failed;
                    }
                    catch
                    {
                        Status = BackgroundTaskStatus.Failed;
                    }
                    raise = true;
                }
                // 已被 MarkKilled / 并发事件收口:跳过翻转,仍走下方 Dispose 释放进程对象
            }
            if (raise)
                Exited?.Invoke();
            // 终态已落且输出已排空:进程对象不再被读路径使用(仅剩 Kill 兜底),释放句柄避免常驻进程累积本机资源
            try { _process?.Dispose(); } catch { /* 已释放:忽略 */ }
        }
        catch
        {
            // async void 回调:异常会直接打到线程池崩掉宿主进程,全部吞掉
        }
    }

    /// <summary>Kill 发现进程已自行退出时推动收敛:与 Exited 事件同一路径(幂等,已退出则按退出码落终态)。</summary>
    internal void ConvergeOnExit() => OnExited();

    internal void MarkKilled()
    {
        bool raise = false;
        lock (_gate)
        {
            if (Status == BackgroundTaskStatus.Running)
            {
                Status = BackgroundTaskStatus.Killed;
                EndedAt = DateTime.Now;
                raise = true;
            }
        }
        if (raise)
            Exited?.Invoke();
    }

    /// <summary>状态摘要(给侧边栏与工具结果共用)。</summary>
    public string StatusText() => Status switch
    {
        BackgroundTaskStatus.Running => "running",
        BackgroundTaskStatus.Completed => "completed",
        BackgroundTaskStatus.Failed => $"failed (exit {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"})",
        BackgroundTaskStatus.Killed => "killed",
        _ => "unknown",
    };

    /// <summary>从绝对字符偏移 offset 起读取新输出(含可能被裁剪的说明);返回新偏移。</summary>
    public (string Text, int NextOffset) ReadOutput(int offset)
    {
        lock (_gate)
        {
            var start = Math.Max(offset, _trimmed);
            if (start >= _trimmed + _output.Length)
                return ("", (int)(_trimmed + _output.Length));
            var text = _output.ToString()[(int)(start - _trimmed)..];
            return (text, (int)(_trimmed + _output.Length));
        }

    }

    /// <summary>完整输出快照(侧边栏查看用)。</summary>
    public string FullOutput()
    {
        lock (_gate)
            return _output.ToString();
    }

    internal Process? KillProcess()
    {
        lock (_gate)
            return _process;
    }
}

/// <summary>
/// 后台任务管理器:单 Agent 一份,内存态(进程不随应用重启存活,故不持久化)。
/// bg_run 启动、bg_output 增量读输出、bg_kill 终止;Changed 供侧边栏刷新。
/// </summary>
public sealed class BackgroundTaskManager : IDisposable
{
    /// <summary>单任务输出缓冲上限(字符)。</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>保留的历史任务上限:超出后从最早结束的已完成任务开始淘汰(Running 永不淘汰)。</summary>
    public const int MaxKeptTasks = 40;

    private readonly object _gate = new();
    private readonly List<BackgroundTask> _tasks = [];
    private int _nextId = 1;

    /// <summary>任务列表或状态变化(新增/结束/被杀)后触发;可能在任意线程。</summary>
    public event Action? Changed;

    /// <summary>启动一个后台命令,返回任务对象(立即返回,不等待结束)。</summary>
    /// <param name="shell">终端 shell(bash / cmd / powershell);为空沿用历史默认 CMD。</param>
    /// <param name="title">任务名称(界面展示用);为空时界面回退显示命令行。</param>
    public BackgroundTask Start(string command, string? workingDirectory, string? shell = null, string title = "")
    {
        BackgroundTask task;
        lock (_gate)
        {
            task = BackgroundTask.Start(_nextId++, command, workingDirectory ?? "", title, MaxOutputChars, shell);
            _tasks.Add(task);
            // 淘汰最早结束的已完成任务,列表不无限增长
            while (_tasks.Count(t => t.Status != BackgroundTaskStatus.Running) > MaxKeptTasks)
            {
                var oldest = _tasks.Where(t => t.Status != BackgroundTaskStatus.Running)
                    .OrderBy(t => t.EndedAt ?? t.StartedAt).FirstOrDefault();
                if (oldest is null)
                    break;
                _tasks.Remove(oldest);
            }
        }
        task.Exited += () => Changed?.Invoke();
        Changed?.Invoke();
        return task;
    }

    /// <summary>按 id 查找任务。</summary>
    public BackgroundTask? Find(int id)
    {
        lock (_gate)
            return _tasks.FirstOrDefault(t => t.Id == id);
    }

    /// <summary>任务快照(新任务在前)。</summary>
    public IReadOnlyList<BackgroundTask> Snapshot()
    {
        lock (_gate)
            return _tasks.AsEnumerable().Reverse().ToList();
    }

    /// <summary>正在运行的任务数(侧边栏角标用)。</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
                return _tasks.Count(t => t.Status == BackgroundTaskStatus.Running);
        }
    }

    /// <summary>从列表移除一个已结束的任务(运行中不可移除,需先 Kill);供侧边栏"清除"。返回是否移除成功。</summary>
    public bool Remove(int id)
    {
        bool removed;
        lock (_gate)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            removed = task is not null && task.Status != BackgroundTaskStatus.Running;
            if (removed)
                _tasks.Remove(task);
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>终止任务(杀整棵进程树);任务不存在或已结束时返回 false。</summary>
    public bool Kill(int id)
    {
        var task = Find(id);
        if (task is null || task.Status != BackgroundTaskStatus.Running)
            return false;
        var killed = false;
        try
        {
            var process = task.KillProcess();
            // 已自行退出的进程不再 Kill:按退出码正常收尾,避免把刚自然结束的任务误标为 Killed(丢退出码)
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                killed = true;
            }
        }
        catch
        {
            // 进程可能恰好在 Kill 前一瞬退出(或句柄失效):按未杀掉处理,由退出路径按真实结局收敛
        }
        if (killed)
            task.MarkKilled();
        else
            task.ConvergeOnExit(); // 未实际杀掉:与 Exited 事件同路径收敛,已退出则按退出码落终态而非 Killed
        Changed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        List<BackgroundTask> running;
        lock (_gate)
            running = _tasks.Where(t => t.Status == BackgroundTaskStatus.Running).ToList();
        foreach (var task in running)
        {
            try
            {
                task.KillProcess()?.Kill(entireProcessTree: true);
            }
            catch
            {
                // 关闭时尽力终止即可
            }
            task.MarkKilled();
        }
    }
}
