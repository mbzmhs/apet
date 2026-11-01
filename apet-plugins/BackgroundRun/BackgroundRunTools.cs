using System.Text.Json;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;
using APet.Framework.Tools.ShellIntent;

namespace APet.Plugins.BackgroundRun;

/// <summary>bg_run:在后台启动一条命令,立即返回任务 id(不等待结束)。</summary>
public sealed class BgRunTool : ToolBase
{
    private readonly BackgroundRunPlugin _plugin;

    public BgRunTool(BackgroundRunPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition => ToolDefinition.Create(
        "bg_run",
        "Start a command in the background and return immediately with a task id. " +
        "Use for long-running or blocking work the user does not need to wait for (dev servers, watchers, builds, installs, downloads, test suites); for quick commands use bash instead. " +
        $"The command runs in {ShellDisplayName()} in the session working directory; follow its progress with bg_output. " +
        "Always set title: a short task name (≤12 chars) shown to the user in the chat UI, and report the returned task id to the user.",
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The command to execute in the background" },
            "title": { "type": "string", "description": "Short task name for the UI, e.g. '安装依赖' (keep within 12 characters)" }
          },
          "required": ["command", "title"]
        }
        """,
        displayName: "后台运行");

    /// <summary>后台命令所用终端的显示名(随宿主推送的 Shell 变化:Git-Bash / CMD / PowerShell)。</summary>
    private string ShellDisplayName()
    {
        var shell = (_plugin.Shell ?? "").Trim().ToLowerInvariant();
        return shell switch
        {
            "bash" or "sh" or "git-bash" => "Git-Bash",
            "powershell" or "pwsh" => "PowerShell",
            _ => "CMD",
        };
    }

    /// <summary>后台命令与终端命令同理:按会话终端类型仿真解析真实意图(路径+读写级别),交安全策略裁决。</summary>
    public override ToolEffect AnalyzeEffect(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var command = doc.RootElement.GetProperty("command").GetString() ?? "";
            var workingDirectory = ToolExecutionScope.WorkingDirectory
                ?? Environment.CurrentDirectory;
            var intent = IntentAnalyze.Analyze(command, workingDirectory, ShellKindResolver.Resolve(_plugin.Shell));
            return new ToolEffect
            {
                Access = intent.StrongestAccess,
                Paths = intent.Paths,
            };
        }
        catch (JsonException)
        {
            return ToolEffect.UnknownEffect;
        }
    }

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        string command, title;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            command = doc.RootElement.GetProperty("command").GetString() ?? "";
            title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command must not be empty.");
        // 未给任务名称时从命令首行兜底(界面灰字不直接铺整条命令)
        if (string.IsNullOrWhiteSpace(title))
        {
            title = (command.Replace('\r', ' ').Replace('\n', ' ')).Trim();
            title = title.Length > 30 ? title[..30] + "…" : title;
        }
        else
        {
            title = title.Trim();
        }

        var task = _plugin.ManagerFor(ToolExecutionScope.SessionId).Start(command, ToolExecutionScope.WorkingDirectory, _plugin.Shell, title);
        return Task.FromResult(
            $"Background task #{task.Id} started (task: {task.Title}, status: {task.StatusText()}): {command}\n" +
            $"Use bg_output with id {task.Id} to check its status and output.");
    }
}

/// <summary>bg_output:按 id 查询后台任务状态与增量输出(offset 之前已返回过的不再重复)。</summary>
public sealed class BgOutputTool : ToolBase
{
    private readonly BackgroundRunPlugin _plugin;

    public BgOutputTool(BackgroundRunPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "bg_output",
        "Read a background task's current status and NEW output since the given offset (0 = all output). " +
        "Pass the 'offset' returned by the previous call to keep following the stream. " +
        "Call it repeatedly (e.g. after doing other work) until the task reports completed or failed, then react to its exit code and output.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The task id (from bg_run)" },
            "offset": { "type": "integer", "description": "Character offset of already-seen output; 0 or omitted returns output from the beginning" }
          },
          "required": ["id"]
        }
        """,
        displayName: "后台输出");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        long id;
        long offset = 0;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            id = doc.RootElement.GetProperty("id").GetInt64();
            if (doc.RootElement.TryGetProperty("offset", out var off))
                offset = off.GetInt64();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }

        var task = _plugin.ManagerFor(ToolExecutionScope.SessionId).Find((int)id);
        if (task is null)
            return Task.FromResult($"Task #{id} not found (it may have been cleaned up). Use bg_list to see current tasks.");
        if (offset < 0)
            offset = 0;

        var (text, nextOffset) = task.ReadOutput((int)offset);
        var sb = new System.Text.StringBuilder($"Task #{task.Id} [{task.StatusText()}] {task.Command}\n");
        if (text.Length == 0)
        {
            sb.Append(offset <= 0
                ? "(no output yet)"
                : "(no new output since the given offset)");
        }
        else
        {
            sb.Append("--- new output ---\n").Append(text.TrimEnd()).Append('\n');
        }
        // 任务已结束且输出已读完:不再给 next offset(无后续可跟,给了反而让模型疑惑是否结束);否则照常
        if (task.Status == BackgroundTaskStatus.Running || text.Length > 0)
            sb.Append($"(next offset: {nextOffset})");
        return Task.FromResult(sb.ToString());
    }
}

/// <summary>bg_wait:阻塞等待一个后台任务结束(带超时),与 bg_output 的单次轮询互补。
/// 任务结束→返回最终状态 + exit code + 自 offset 起的输出;超时→报告"仍在运行"并附当前增量输出,
/// 由模型决定再等 / 轮询 / 终止。全程 await 不占线程,接入宿主取消令(停止运行即返回当前状态)。</summary>
public sealed class BgWaitTool : ToolBase
{
    private readonly BackgroundRunPlugin _plugin;

    public BgWaitTool(BackgroundRunPlugin plugin) => _plugin = plugin;

    /// <summary>默认等待上限:600 秒。</summary>
    private const int DefaultTimeoutMs = 600_000;
    /// <summary>等待上限(1 小时),防止误传把轮次长时间卡死。</summary>
    private const int MaxTimeoutMs = 3_600_000;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "bg_wait",
        "Block until a background task finishes or a timeout elapses (unlike bg_output, which is a single poll). " +
        "On finish, returns the final status (completed/failed/killed), exit code and output since 'offset'. " +
        $"On timeout (default {DefaultTimeoutMs / 1000}s, capped at {MaxTimeoutMs / 1000}s) it reports the task is still running " +
        "so you can wait again, poll with bg_output, or terminate with bg_kill.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The task id (from bg_run)" },
            "timeout_ms": { "type": "integer", "description": "Maximum time to wait in milliseconds (0 = do not wait, just report the current state)" },
            "offset": { "type": "integer", "description": "Character offset of already-seen output; 0 or omitted returns output from the beginning" }
          },
          "required": ["id"]
        }
        """,
        displayName: "后台等待");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        int id;
        var timeoutMs = DefaultTimeoutMs;
        var offset = 0;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            id = (int)doc.RootElement.GetProperty("id").GetInt64();
            if (doc.RootElement.TryGetProperty("timeout_ms", out var t))
                timeoutMs = (int)Math.Clamp(t.GetInt64(), 0, MaxTimeoutMs);
            if (doc.RootElement.TryGetProperty("offset", out var off))
                offset = (int)off.GetInt64();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
        if (offset < 0)
            offset = 0;

        var task = _plugin.ManagerFor(ToolExecutionScope.SessionId).Find(id);
        if (task is null)
            return $"Task #{id} not found (it may have been cleaned up). Use bg_list to see current tasks.";

        // 已结束:无需等待,直接返回最终结果
        if (task.Status != BackgroundTaskStatus.Running)
            return FormatResult(task, offset);

        // 尚未结束:订阅 Exited 等待结束;超时或宿主取消则提前返回
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onExited = () => ended.TrySetResult();
        task.Exited += onExited;
        // 竞态兜底:状态检查与订阅之间任务可能恰好结束(Exited 已触发但未被订阅到)
        if (task.Status != BackgroundTaskStatus.Running)
            ended.TrySetResult();
        try
        {
            var winner = await Task.WhenAny(ended.Task, Task.Delay(timeoutMs, cancellationToken));
            if (cancellationToken.IsCancellationRequested)
                return FormatCancelled(task, offset);
            if (winner == ended.Task)
                return FormatResult(task, offset);
            return FormatTimeout(task, offset, timeoutMs / 1000);
        }
        finally
        {
            task.Exited -= onExited;
        }
    }

    private static string FormatResult(BackgroundTask task, int offset)
    {
        var (text, nextOffset) = task.ReadOutput(offset);
        var sb = new System.Text.StringBuilder($"Task #{task.Id} [{task.StatusText()}] {task.Command}");
        if (task.ExitCode is int code)
            sb.Append($" (exit {code})");
        sb.Append('\n');
        sb.Append(text.Length == 0 ? "(no output since the given offset)" : "--- output ---\n" + text.TrimEnd());
        if (text.Length > 0)
            sb.Append($"\n(next offset: {nextOffset})"); // 已结束且已读完:不给 next offset
        return sb.ToString();
    }

    private static string FormatTimeout(BackgroundTask task, int offset, int waitedSec)
    {
        var (text, nextOffset) = task.ReadOutput(offset);
        var sb = new System.Text.StringBuilder($"Task #{task.Id} still running after {waitedSec}s: {task.Command}\n");
        sb.Append(text.Length == 0
            ? "(no new output since the given offset)"
            : "--- new output ---\n" + text.TrimEnd());
        sb.Append($"\n(next offset: {nextOffset})");
        sb.Append("\nCall bg_wait again to keep waiting, bg_output to poll, or bg_kill to terminate.");
        return sb.ToString();
    }

    private static string FormatCancelled(BackgroundTask task, int offset)
    {
        return task.Status == BackgroundTaskStatus.Running
            ? $"Task #{task.Id} [running] {task.Command}\nWait interrupted (run stopped); the task is still running in the background. Use bg_output/bg_wait to follow it or bg_kill to terminate."
            : FormatResult(task, offset);
    }
}

/// <summary>bg_kill:终止一个后台任务(整棵进程树)。</summary>
public sealed class BgKillTool : ToolBase
{
    private readonly BackgroundRunPlugin _plugin;

    public BgKillTool(BackgroundRunPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "bg_kill",
        "Terminate a background task (kills the whole process tree). Use when a task is no longer needed " +
        "or was started by mistake. Before finishing your work, do not leave orphaned servers or watchers running unless the user wants them.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The task id (from bg_run)" }
          },
          "required": ["id"]
        }
        """,
        displayName: "后台终止");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        long id;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            id = doc.RootElement.GetProperty("id").GetInt64();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }

        return Task.FromResult(_plugin.ManagerFor(ToolExecutionScope.SessionId).Kill((int)id)
            ? $"Task #{id} terminated."
            : $"Task #{id} not found or already finished.");
    }
}

/// <summary>bg_list:列出本 Agent 的全部后台任务(新任务在前)。</summary>
public sealed class BgListTool : ToolBase
{
    private readonly BackgroundRunPlugin _plugin;

    public BgListTool(BackgroundRunPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "bg_list",
        "List all background tasks of this agent (newest first) with their ids, commands and statuses; the list is shown live in the user's sidebar.",
        """{ "type": "object", "properties": {} }""",
        displayName: "后台列表");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var tasks = _plugin.ManagerFor(ToolExecutionScope.SessionId).Snapshot();
        if (tasks.Count == 0)
            return Task.FromResult("No background tasks.");
        var sb = new System.Text.StringBuilder();
        foreach (var task in tasks)
            sb.Append($"#{task.Id} [{task.StatusText()}] {task.Command}\n");
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
