using System.Text.Json;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;

namespace APet.Plugins.SubAgent;

/// <summary>sub_run:启动一个预配置子代理(后台运行,立即返回运行 id;不等待结束)。</summary>
public sealed class SubRunTool : ToolBase
{
    private readonly SubAgentPlugin _plugin;

    public SubRunTool(SubAgentPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "sub_run",
        "Delegate a self-contained sub-task to a pre-configured subagent. It runs an independent conversation " +
        "(its own system prompt and toolset) and CANNOT see this conversation — the prompt must contain ALL context it needs. " +
        "Returns a run id immediately; use sub_wait(id) to block until it finishes and receive its final answer. " +
        "Always set title: a short task name (≤12 chars) shown to the user in the chat UI.",
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The subagent name (see the available subagents in your instructions)" },
            "title": { "type": "string", "description": "Short task name for the UI, e.g. '抓取天气预报' (keep within 12 characters)" },
            "prompt": { "type": "string", "description": "The complete task prompt for the subagent, including all needed context" }
          },
          "required": ["name", "prompt", "title"]
        }
        """,
        displayName: "发起子代理");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        string name, prompt, title;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            name = doc.RootElement.GetProperty("name").GetString() ?? "";
            title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Both 'name' and 'prompt' must not be empty.");
        // 未给任务名称时从 prompt 首行兜底(界面灰字不直接铺整段正文)
        if (string.IsNullOrWhiteSpace(title))
        {
            title = (prompt.Replace('\r', ' ').Replace('\n', ' ')).Trim();
            title = title.Length > 30 ? title[..30] + "…" : title;
        }
        else
        {
            title = title.Trim();
        }

        var def = _plugin.FindDefinition(name);
        if (def is null)
        {
            var available = string.Join(", ", _plugin.Definitions.Select(d => d.Name));
            return Task.FromResult($"Subagent '{name}' not found. Available subagents: {available}.");
        }
        // 预检查只是快速失败(免得白建 LLM 连接);权威判定在 Manager.Start 的登记锁内,
        // 预检查与登记之间的窗口由锁内复查兜住,并发 sub_run 不会突破上限(Launch 返回 null 走同一提示)
        if (!_plugin.Manager.CanStart())
            return Task.FromResult(TooManyMessage());

        // 继承主会话的安全策略(工作目录/权限/信任目录);未接线时回退最小策略
        var policy = _plugin.PolicyResolver?.Invoke(ToolExecutionScope.SessionId)
            ?? new AgentSecurityPolicy { WorkingDirectory = ToolExecutionScope.WorkingDirectory };
        var run = _plugin.Launch(def, prompt.Trim(), title, policy, cancellationToken);
        if (run is null)
            return Task.FromResult(TooManyMessage());
        return Task.FromResult(
            $"Subagent {def.Name} started (run #{run.Id}, task: {run.Title}). " +
            $"Use sub_wait with id {run.Id} to wait for its result.");
    }

    private static string TooManyMessage() =>
        $"Too many subagents running (max {SubAgentRunManager.MaxConcurrentRuns}). " +
        "Wait for one to finish with sub_wait, or terminate one with sub_kill.";
}

/// <summary>sub_wait:阻塞等待一个子代理结束(带超时),完成后返回其最后助手正文(即子任务结果)。
/// 与 sub_run 的立即返回互补:先 sub_run 再 sub_wait,或一次等待多个 id。</summary>
public sealed class SubWaitTool : ToolBase
{
    private readonly SubAgentPlugin _plugin;

    /// <summary>结果正文回传上限(字符),超出截断(全量仍可在侧边栏/模态框查看)。</summary>
    private const int MaxResultChars = 32_000;

    public SubWaitTool(SubAgentPlugin plugin) => _plugin = plugin;

    /// <summary>默认等待上限:600 秒。</summary>
    private const int DefaultTimeoutMs = 600_000;
    /// <summary>等待上限(1 小时),防止误传把轮次长时间卡死。</summary>
    private const int MaxTimeoutMs = 3_600_000;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "sub_wait",
        "Block until a subagent run finishes or a timeout elapses (sub_run returns immediately). " +
        "On finish, returns the subagent's final answer as the result text. " +
        $"On timeout (default {DefaultTimeoutMs / 1000}s, capped at {MaxTimeoutMs / 1000}s) it reports the run is still running " +
        "so you can wait again. If the run was cancelled it reports that.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The run id (from sub_run)" },
            "timeout_ms": { "type": "integer", "description": "Maximum time to wait in milliseconds (0 = do not wait, just report the current state)" }
          },
          "required": ["id"]
        }
        """,
        displayName: "等待子代理");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        int id;
        var timeoutMs = DefaultTimeoutMs;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            id = (int)doc.RootElement.GetProperty("id").GetInt64();
            if (doc.RootElement.TryGetProperty("timeout_ms", out var t))
                timeoutMs = (int)Math.Clamp(t.GetInt64(), 0, MaxTimeoutMs);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }

        var run = _plugin.Manager.Find(id, ToolExecutionScope.SessionId);
        if (run is null)
            return $"Subagent run #{id} not found in this conversation (it may have been cleaned up, or belongs to another conversation). Use sub_list to see current runs.";
        if (run.Status != SubAgentStatus.Running)
            return FormatResult(run);

        // 尚未结束:订阅状态迁移等待;超时或宿主取消则提前返回
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action onStateChanged = () => ended.TrySetResult();
        run.StateChanged += onStateChanged;
        // 竞态兜底:状态检查与订阅之间运行可能恰好结束
        if (run.Status != SubAgentStatus.Running)
            ended.TrySetResult();
        try
        {
            var winner = await Task.WhenAny(ended.Task, Task.Delay(timeoutMs, cancellationToken));
            if (cancellationToken.IsCancellationRequested)
                return FormatCancelled(run);
            if (winner == ended.Task)
                return FormatResult(run);
            return FormatTimeout(run, timeoutMs / 1000);
        }
        finally
        {
            run.StateChanged -= onStateChanged;
        }
    }

    /// <summary>结果文本统一带 (run #N, task: 任务名) 标记:宿主据此把任务名称显示在工具行上。</summary>
    private static string Head(SubAgentRun run) => $"Subagent {run.Name} (run #{run.Id}, task: {run.Title})";

    private static string FormatResult(SubAgentRun run)
    {
        return run.Status switch
        {
            SubAgentStatus.Completed =>
                $"{Head(run)} completed:\n{Truncate(run.FinalContent ?? "")}",
            SubAgentStatus.Failed =>
                $"{Head(run)} failed: {run.Error}",
            SubAgentStatus.Cancelled =>
                $"{Head(run)} was cancelled.",
            _ => FormatTimeout(run, 0),
        };
    }

    private static string FormatTimeout(SubAgentRun run, int waitedSec)
    {
        return $"{Head(run)} still running after {waitedSec}s. " +
               "Call sub_wait again to keep waiting, or sub_kill to terminate it.";
    }

    private static string FormatCancelled(SubAgentRun run)
    {
        return run.Status == SubAgentStatus.Running
            ? $"{Head(run)} is still running; the wait was interrupted (main run stopped)."
            : FormatResult(run);
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaxResultChars)
            return text;
        return text[..MaxResultChars] + $"\n…(truncated, {text.Length - MaxResultChars} more chars; full transcript in the agent sidebar)";
    }
}

/// <summary>sub_tell:给一个运行中的子代理传话(临时指派/纠偏/补充上下文)。
/// 投递到其会话队列(下一个请求前被收编,当作用户消息继续工作),阻塞等待其回复后返回;
/// 子代理回复后继续原任务。运行已结束时不投递,直接把该情况告知主 Agent。</summary>
public sealed class SubTellTool : ToolBase
{
    private readonly SubAgentPlugin _plugin;

    /// <summary>回复正文回传上限(字符),超出截断(完整对话在子代理模态框查看)。</summary>
    private const int MaxReplyChars = 16_000;

    /// <summary>默认等待上限:600 秒;0 = 只投递不等回复。</summary>
    private const int DefaultTimeoutMs = 600_000;
    private const int MaxTimeoutMs = 3_600_000;

    public SubTellTool(SubAgentPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "sub_tell",
        "Pass a mid-run instruction to a RUNNING subagent: steer, correct, or add context while it keeps working. " +
        "The message is delivered before its next request (as a user message in its conversation); this call blocks until " +
        "the subagent replies in text, then returns the reply — the subagent continues its task afterwards. " +
        "If the run has already finished, the tool reports that instead of delivering.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The run id (from sub_run)" },
            "text": { "type": "string", "description": "The instruction or context to pass to the subagent" },
            "timeout_ms": { "type": "integer", "description": "Maximum time to wait for the reply in milliseconds (default 600000; 0 = deliver without waiting)" }
          },
          "required": ["id", "text"]
        }
        """,
        displayName: "传话子代理");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        long id;
        var timeoutMs = DefaultTimeoutMs;
        string text;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            id = doc.RootElement.GetProperty("id").GetInt64();
            text = doc.RootElement.GetProperty("text").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("timeout_ms", out var t))
                timeoutMs = (int)Math.Clamp(t.GetInt64(), 0, MaxTimeoutMs);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("'text' must not be empty.");

        var run = _plugin.Manager.Find((int)id, ToolExecutionScope.SessionId);
        if (run is null)
            return $"Subagent run #{id} not found in this conversation (it may have been cleaned up, or belongs to another conversation). Use sub_list to see current runs.";

        var replyTask = run.EnqueueTell(text, out var error);
        if (replyTask is null)
            return $"Subagent {run.Name} (run #{run.Id}, task: {run.Title}): {error}";

        if (timeoutMs == 0)
            return $"{Head(run)}: message delivered (not waiting for a reply).";

        // 等回复:回复 / 运行终止 / 超时或主轮取消,三路竞争
        var winner = await Task.WhenAny(replyTask, Task.Delay(timeoutMs, cancellationToken));
        if (cancellationToken.IsCancellationRequested)
            return $"{Head(run)}: message delivered; the wait was interrupted (main run stopped). The message stays in the subagent's queue/context.";
        if (winner != replyTask)
            return $"{Head(run)}: message delivered, no reply within {timeoutMs / 1000}s. " +
                   "The subagent will pick it up and keep working; use sub_wait for its result or sub_list to check status.";

        var reply = await replyTask;
        if (reply is null)
        {
            return run.Status switch
            {
                SubAgentStatus.Failed => $"{Head(run)} failed: {run.Error}. The message was not processed.",
                SubAgentStatus.Cancelled => $"{Head(run)} was cancelled; the message was not processed.",
                SubAgentStatus.Running => $"{Head(run)}: message delivered, but no text reply yet (it is still working). Use sub_wait for its result, or sub_tell again later.",
                _ => $"{Head(run)} completed. The message arrived with its final request, so the final answer below is its response:\n{Truncate(run.FinalContent ?? "")}",
            };
        }
        var tail = run.Status == SubAgentStatus.Running
            ? "The subagent keeps working on its task."
            : $"The subagent has finished ({run.Status.ToString().ToLowerInvariant()}) right after this reply.";
        return $"{Head(run)} replied:\n{Truncate(reply)}\n({tail})";
    }

    private static string Head(SubAgentRun run) => $"Subagent {run.Name} (run #{run.Id}, task: {run.Title})";

    private static string Truncate(string text)
    {
        if (text.Length <= MaxReplyChars)
            return text;
        return text[..MaxReplyChars] + $"\n…(truncated, {text.Length - MaxReplyChars} more chars; full transcript in the subagent modal)";
    }
}

/// <summary>sub_list:列出本 Agent 的全部子代理运行(新运行在前)。</summary>
public sealed class SubListTool : ToolBase
{
    private readonly SubAgentPlugin _plugin;

    public SubListTool(SubAgentPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "sub_list",
        "List the subagent runs of this conversation (newest first) with their ids, names, prompts and statuses. " +
        "Runs started by other conversations are not visible here.",
        """{ "type": "object", "properties": {} }""",
        displayName: "子代理列表");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        // 按主会话过滤:子代理运行与 session 绑定,只列出本对话发起的运行
        var runs = _plugin.Manager.Snapshot(ToolExecutionScope.SessionId);
        if (runs.Count == 0)
            return Task.FromResult("No subagent runs in this conversation.");
        var sb = new System.Text.StringBuilder();
        foreach (var run in runs)
            sb.Append($"#{run.Id} [{run.Status}] {run.Name} — {FirstLine(run.Prompt)}\n");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    private static string FirstLine(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length > 80 ? line[..77] + "…" : line;
    }
}

/// <summary>sub_kill:终止一个仍在运行的子代理。</summary>
public sealed class SubKillTool : ToolBase
{
    private readonly SubAgentPlugin _plugin;

    public SubKillTool(SubAgentPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "sub_kill",
        "Terminate a running subagent run. Use when a subagent is no longer needed or was started by mistake.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The run id (from sub_run)" }
          },
          "required": ["id"]
        }
        """,
        displayName: "终止子代理");

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

        var run = _plugin.Manager.Find((int)id, ToolExecutionScope.SessionId);
        return Task.FromResult(run?.Cancel() == true
            ? $"Subagent (run #{id}, task: {run.Title}) terminated."
            : $"Subagent run #{id} not found in this conversation or already finished.");
    }
}
