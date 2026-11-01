using System.Text;
using APet.Framework.Agent;
using APet.Framework.Events;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;
using APet.UI.Services;

namespace APet.UI.Web;

/// <summary>
/// 把 Agent 流式回调发布为 Web SSE 事件(与会话总线一一对应)。
/// 工具行复用 ToolCallPresenter 的展示格式化,浏览器工具卡与 WPF 版观感一致;
/// 授权不在本类裁决(鉴权卡片经 WebEventHub 登记、REST 解析)。
/// </summary>
public sealed class SseRelay : AgentCallbacks
{
    private readonly string _agentId;
    private readonly string _sessionId;
    private readonly Func<string, string> _displayName;
    private readonly string? _workdir;
    private readonly SessionStore _store;
    private readonly Func<APet.Framework.LLM.LlmUsage, double>? _costResolver;
    private readonly Dictionary<string, int> _argsKeyByCallId = new(); // callId → 参数归并键(流期记录,tool_started 回填给前端替换占位行)

    /// <summary>子代理运行 id → 任务名称(宿主注入;sub_wait/sub_kill 行启动时回填,运行期间不显示"运行 #N")。</summary>
    public Func<int, string?>? RunTitleResolver { get; set; }

    public SseRelay(string agentId, string sessionId, Func<string, string> displayNameResolver, string? workdir,
        Func<APet.Framework.LLM.LlmUsage, double>? costResolver = null)
    {
        _agentId = agentId;
        _sessionId = sessionId;
        _displayName = displayNameResolver;
        _workdir = workdir;
        _store = SessionStore.Shared(agentId); // 共享单例:每轮新建会各开一条 SQLite 连接且从不关闭
        _costResolver = costResolver;
    }

    public override Task OnThinkingDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default)
    {
        if (delta.Length > 0)
            Publish("thinking_delta", new { delta });
        return Task.CompletedTask;
    }

    public override Task OnContentDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default)
    {
        if (delta.Length > 0)
            Publish("content_delta", new { delta }); // 空增量不发:前端会因此多推空气泡段
        return Task.CompletedTask;
    }

    public override Task OnCompletedAsync(AgentSession session, AgentResult result, CancellationToken cancellationToken = default)
    {
        Publish("complete");
        return Task.CompletedTask;
    }

    public override Task OnTurnStartedAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default)
    {
        // 本轮请求开始:context 助手消息 id 随帧发出(预留给前端按消息挂载步骤)。
        // 此帧在 POST 请求体的 dispatcher.Invoke 内同步发布、可能先于 202 到达浏览器——
        // 前端必须早退,不得据此建气泡(否则用户消息入列表前凭空多一个占位,见 ChatView handleEvent)
        Publish("turn_started", new { msgId = assistantMessage.Id });
        return Task.CompletedTask;
    }

    public override Task OnToolCallArgsDeltaAsync(AgentSession session, int argsKey, string? callId, string? toolName, string argsSnapshot, CancellationToken cancellationToken = default)
    {
        if (callId is not null)
            _argsKeyByCallId[callId] = argsKey;
        var (path, addLines, delLines) = PeekPartialArgs(argsSnapshot);
        Publish("tool_args", new
        {
            argsKey,
            callId,
            toolName,
            displayName = _displayName(toolName ?? ""), // 全工具统一走注册表中文名(bash=运行终端等,与 started/finished 同源)
            path,
            addLines,
            delLines,
            argsBytes = argsSnapshot.Length,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 半截 JSON 的尽力解析:①参数已完整(冲刷末态帧)则原样直解;②未完整则补全未闭合的
    /// 字符串/花括号后解析;③再失败退化到正则抠 path。行数统计按值内换行近似,供前端
    /// "生成中 +N/−N"实时预览——数字只增不变准,最终以 tool_started/tool_finished 的完整解析为准。
    /// </summary>
    private static (string? Path, long Add, long Del) PeekPartialArgs(string partial)
    {
        // ① 原样直解:补全会给完整 JSON 多挂一个 } 必炸,完整与半截必须分路
        if (TryReadArgs(partial, out var path, out var add, out var del))
            return (path, add, del);

        // ② 半截 JSON:补全未闭合的字符串/花括号后重试
        var sb = new StringBuilder(partial.Length + 8);
        var inStr = false;
        var esc = false;
        foreach (var ch in partial)
        {
            sb.Append(ch);
            if (esc) { esc = false; continue; }
            if (ch == '\\' && inStr) { esc = true; continue; }
            if (ch == '"') inStr = !inStr;
        }
        if (inStr)
        {
            if (sb.Length > 0 && sb[^1] == '\\') sb.Length--; // 半个转义序列
            sb.Append('"');
        }
        sb.Append('}');
        if (TryReadArgs(sb.ToString(), out path, out add, out del))
            return (path, add, del);

        // ③ 补全仍失败(键名半截等):退化到正则抠 path,行数留 0
        var m = System.Text.RegularExpressions.Regex.Match(partial, "\"path\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)");
        if (m.Success)
            path = System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value);
        return (path, 0, 0);
    }

    /// <summary>解析工具参数对象:path 与四个内容键的行数统计。非对象/解析失败返回 false。</summary>
    private static bool TryReadArgs(string json, out string? path, out long add, out long del)
    {
        path = null;
        add = 0;
        del = 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("path", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                path = p.GetString();
            foreach (var (key, isDel) in new[] { ("old_string", true), ("new_string", false), ("content", false), ("patch", false) })
            {
                if (!root.TryGetProperty(key, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                var text = v.GetString() ?? "";
                var lines = text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + (text.EndsWith('\n') ? 0 : 1);
                if (isDel) del = lines;
                else add = Math.Max(add, lines);
            }
            return path is not null || add > 0 || del > 0; // 至少解析到点东西才算数
        }
        catch
        {
            return false;
        }
    }

    public override Task OnToolCallStartedAsync(AgentSession session, ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var row = ToolCallPresenter.Create(_displayName(toolCall.Name), toolCall.Name, toolCall.ArgumentsJson, _workdir);
        ToolCallPresenter.AttachSubRunTitle(row, RunTitleResolver); // 与 WPF 回调同口径:运行期间即显示任务名
        Publish("tool_started", new
        {
            toolName = row.ToolName,
            toolCallId = toolCall.Id, // 与快照 ToolStep 同构:工具行回链 context 消息
            argsKey = toolCall.Id is { Length: > 0 } id && _argsKeyByCallId.TryGetValue(id, out var ak) ? ak : (int?)null,
            displayName = row.DisplayName,
            summary = row.Summary,
            detail = row.DetailBody, // 运行中即可展开看完整内容(如 bash 的完整命令)
            hasDetail = row.HasDetail,
            pathLink = row.PathLink,
            statAdd = row.StatAdd,
            statDel = row.StatDel,
            diffLines = row.DiffLines.Select(d => new { lineNo = d.LineNo, text = d.Text, kind = d.Kind }).ToList(),
            subRunId = row.SubRunId, // sub_* 工具:行可点击打开子代理模态框
            subRunTitle = row.SubRunTitle, // 任务名称:点击入口显示它而非"运行 #N"
        });
        return Task.CompletedTask;
    }

    /// <summary>工具执行中的实时输出增量(运行终端工具推送):帧小、频率受工具侧节流控制。</summary>
    public override Task OnToolCallOutputAsync(AgentSession session, ToolCall toolCall, string stream, string delta, CancellationToken cancellationToken = default)
    {
        if (delta.Length > 0)
            Publish("tool_output", new { toolCallId = toolCall.Id, toolName = toolCall.Name, stream, delta });
        return Task.CompletedTask;
    }

    public override Task OnToolCallFinishedAsync(AgentSession session, ToolCall toolCall, ToolResult result, CancellationToken cancellationToken = default)
    {
        // 结果阶段重建行再套用格式化:摘要/详情/diff 与 WPF 展示同一套规则
        var row = ToolCallPresenter.Create(_displayName(toolCall.Name), toolCall.Name, toolCall.ArgumentsJson, _workdir);
        ToolCallPresenter.ApplyResult(row, result.Text);
        // 多模态结果图片(如截图)落盘会话图片目录,帧里带文件名让浏览器经 images 端点取图——
        // 不等回合结束的会话快照,运行中展开即可看
        var imageFiles = new List<string>();
        foreach (var dataUrl in result.ImageDataUrls)
        {
            var name = _store.StoreImageDataUrl(_sessionId, dataUrl);
            if (name is not null)
                imageFiles.Add(name);
        }
        Publish("tool_finished", new
        {
            toolName = row.ToolName,
            toolCallId = toolCall.Id, // 与快照 ToolStep 同构:工具行回链 context 消息
            displayName = row.DisplayName,
            summary = row.Summary,
            detail = row.DetailBody,
            hasDetail = row.HasDetail || imageFiles.Count > 0,
            resultSummary = row.ResultSummary,
            isError = row.IsError,
            pathLink = row.PathLink,
            statAdd = row.StatAdd,
            statDel = row.StatDel,
            diffLines = row.DiffLines.Select(d => new { lineNo = d.LineNo, text = d.Text, kind = d.Kind }).ToList(),
            imageFiles,
            subRunId = row.SubRunId, // sub_* 工具:行可点击打开子代理模态框
            subRunTitle = row.SubRunTitle, // 任务名称:点击入口显示它而非"运行 #N"
        });
        return Task.CompletedTask;
    }

    public override Task OnErrorAsync(AgentSession session, Exception exception, CancellationToken cancellationToken = default)
    {
        Publish("error", new { message = exception.Message });
        return Task.CompletedTask;
    }

    public override Task OnUsageReportedAsync(AgentSession session, Framework.LLM.LlmUsage usage, CancellationToken cancellationToken = default)
    {
        // 逐请求完整用量(前端可据此做成本/统计;promptTokens 保留旧字段名兼容)
        Publish("usage", new
        {
            promptTokens = usage.PromptTokens,
            completionTokens = usage.CompletionTokens,
            totalTokens = usage.TotalTokens,
            cachedTokens = usage.CachedTokens, // Prompt 缓存命中部分(状态栏缓存命中率的分子)
            tps = usage.Tps, // 本次请求输出速度(状态栏"TPS 最新")
            cost = _costResolver?.Invoke(usage) ?? 0, // 本次请求费用(元;状态栏累计)
        });
        return Task.CompletedTask;
    }

    private void Publish(string type, object? data = null) =>
        WebEventHub.Publish(_agentId, _sessionId, type, data);
}

/// <summary>
/// 主回调(展示 VM + 持久化,即 UiCallbacks)与 Web 流式转发合并为一条回调链;
/// 授权仅由主回调裁决(鉴权卡片 + Web REST 解析),转发链不参与。
/// </summary>
public sealed class CompositeCallbacks : AgentCallbacks
{
    private readonly AgentCallbacks _primary;
    private readonly AgentCallbacks? _secondary;

    public CompositeCallbacks(AgentCallbacks primary, AgentCallbacks? secondary)
    {
        _primary = primary;
        _secondary = secondary;
    }

    public override Task OnTurnStartedAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default) =>
        Both(c => c.OnTurnStartedAsync(session, assistantMessage, cancellationToken));

    public override Task OnThinkingDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default) =>
        Both(c => c.OnThinkingDeltaAsync(session, delta, cancellationToken));

    public override Task OnContentDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default) =>
        Both(c => c.OnContentDeltaAsync(session, delta, cancellationToken));

    public override Task OnToolCallStartedAsync(AgentSession session, ToolCall toolCall, CancellationToken cancellationToken = default) =>
        Both(c => c.OnToolCallStartedAsync(session, toolCall, cancellationToken));

    public override Task OnToolCallArgsDeltaAsync(AgentSession session, int argsKey, string? callId, string? toolName, string argsSnapshot, CancellationToken cancellationToken = default) =>
        Both(c => c.OnToolCallArgsDeltaAsync(session, argsKey, callId, toolName, argsSnapshot, cancellationToken));

    public override Task OnToolCallFinishedAsync(AgentSession session, ToolCall toolCall, ToolResult result, CancellationToken cancellationToken = default) =>
        Both(c => c.OnToolCallFinishedAsync(session, toolCall, result, cancellationToken));

    public override async Task<AuthorizationResult> OnToolCallAuthorizationAsync(
        AgentSession session, ToolCall toolCall, ToolEffect effect, AuthorizationResult decision,
        CancellationToken cancellationToken = default) =>
        await _primary.OnToolCallAuthorizationAsync(session, toolCall, effect, decision, cancellationToken);

    public override Task OnCompletedAsync(AgentSession session, AgentResult result, CancellationToken cancellationToken = default) =>
        Both(c => c.OnCompletedAsync(session, result, cancellationToken));

    public override Task OnErrorAsync(AgentSession session, Exception exception, CancellationToken cancellationToken = default) =>
        Both(c => c.OnErrorAsync(session, exception, cancellationToken));

    public override Task OnUsageReportedAsync(AgentSession session, Framework.LLM.LlmUsage usage, CancellationToken cancellationToken = default) =>
        Both(c => c.OnUsageReportedAsync(session, usage, cancellationToken));

    private async Task Both(Func<AgentCallbacks, Task> action)
    {
        await action(_primary);
        if (_secondary is { } secondary)
            await action(secondary);
    }
}
