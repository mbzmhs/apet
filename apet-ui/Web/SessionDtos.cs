using APet.UI.Services;
using APet.UI.ViewModels;

namespace APet.UI.Web;

/// <summary>
/// 会话快照 DTO:经 Dispatcher 在 UI 线程把 SessionViewModel 拍平为纯数据,
/// 再在线程外序列化给 REST。步骤流与 SSE 事件共用同一形状,前端两种来源可互换渲染。
/// </summary>
public static class SessionSnapshot
{
    /// <summary>会话快照。消息窗口参数(均为 null = 全量,兼容旧调用):
    /// limit = 取末尾 limit 行(首屏窗口/尾部刷新);after = 取 seq &gt; after 的全部行(刷新已加载窗口,含尾部新增);
    /// before = 取 seq &lt; before 的末尾 limit 行(向上翻页取更早历史)。
    /// 窗口信息回带在 DTO 尾三字段:MessageTotal/MessageCursor(窗口首行 seq,全量=null)/HasMoreMessages。</summary>
    public static SessionStateDto Of(SessionViewModel session, int contextLimit = 0,
        BackgroundConfig? chatBg = null, BackgroundConfig? windowBg = null,
        int? limit = null, int? after = null, int? before = null)
    {
        // 非临时行编号 seq = 跳过 Ephemeral 行后的序号(与 display 表落盘口径一致,前缀删除前保持稳定);
        // 临时行(压缩齿轮等)只出现在尾部,seq 为 null
        var rows = new List<(int Seq, ChatMessageViewModel M)>();
        var ephemerals = new List<ChatMessageViewModel>();
        var seq = 0;
        foreach (var m in session.Messages)
        {
            if (m.Ephemeral) ephemerals.Add(m);
            else rows.Add((seq++, m));
        }

        List<(int? Seq, ChatMessageViewModel M)> picked;
        int? cursor = null;
        var hasMore = false;

        if (after is int a)
        {
            // 刷新已加载窗口:窗口首行起的全部行 + 尾部新增 + 临时行;
            // 窗口首行已不存在(如压缩重排/删除)时回落末尾 limit 行,窗口重置
            picked = rows.Where(r => r.Seq > a).Select(r => ((int?)r.Seq, r.M)).ToList();
            if (picked.Count == 0)
                picked = Tail(rows, limit);
            picked.AddRange(ephemerals.Select(m => ((int?)null, m)));
            cursor = picked.Count > 0 ? picked[0].Seq : null;
            hasMore = (cursor ?? -1) > 0;
        }
        else if (before is int b)
        {
            // 向上翻页:seq < before 的末尾 limit 行(升序返回)
            var take = Math.Max(1, limit ?? 100);
            var last = -1;
            for (var i = rows.Count - 1; i >= 0; i--)
                if (rows[i].Seq < b) { last = i; break; }
            if (last < 0)
            {
                picked = []; // 无更早行(或游标已被压缩重排):翻页结束
                hasMore = false;
            }
            else
            {
                var start = Math.Max(0, last - take + 1);
                picked = rows.GetRange(start, last - start + 1).Select(r => ((int?)r.Seq, r.M)).ToList();
                cursor = rows[start].Seq;
                hasMore = start > 0;
            }
        }
        else if (limit is int l && l > 0)
        {
            // 首屏窗口:末尾 l 行 + 临时行
            picked = Tail(rows, l);
            picked.AddRange(ephemerals.Select(m => ((int?)null, m)));
            cursor = picked.Count > 0 ? picked[0].Seq : null;
            hasMore = (cursor ?? -1) > 0;
        }
        else
        {
            // 全量(旧行为):含临时条目(压缩中"上下文压缩中"文本行),过滤掉的话压缩进行中的网页快照永远看不到该行
            picked = rows.Select(r => ((int?)r.Seq, r.M)).ToList();
            picked.AddRange(ephemerals.Select(m => ((int?)null, m)));
        }

        return new SessionStateDto(
            session.SessionId, session.Title, session.IsStreaming, session.IsCompressing, session.HasError,
            session.ChatProvider, session.ChatModel, session.ThinkingEffort,
            session.WorkingDirectory, session.WorkingDirectoryAccess, session.IsWorkDirLocked,
            session.LastPromptTokens, session.LastMessageTimeText,
            contextLimit,
            new SessionUsageDto(session.UsagePromptTotal, session.UsageCachedTotal, session.UsageCompletionTotal, session.UsageLastTps, session.UsageCost),
            chatBg is { } bg && !string.IsNullOrWhiteSpace(bg.ImagePath) ? bg.OpacityPercent : null,
            chatBg?.Stretch,
            windowBg is { } wb && !string.IsNullOrWhiteSpace(wb.ImagePath) ? wb.OpacityPercent : null,
            windowBg?.Stretch,
            [.. picked.Select(p => Message(p.M, p.Seq))],
            QueuedMessagesOf(session),
            rows.Count,
            cursor,
            hasMore,
            session.IsArchived);
    }

    private static List<(int? Seq, ChatMessageViewModel M)> Tail(List<(int Seq, ChatMessageViewModel M)> rows, int? limit)
    {
        var l = Math.Max(1, limit ?? 100);
        var start = Math.Max(0, rows.Count - l);
        return rows.GetRange(start, rows.Count - start).Select(r => ((int?)r.Seq, r.M)).ToList();
    }

    /// <summary>中途队列快照(轻量展示字段):REST 快照与 queue_changed 事件载荷共用,保证两处形状一致。
    /// 完整原文与附件走 DELETE 按条取回,避免每次搬运大附件。</summary>
    public static List<QueuedItemDto> QueuedMessagesOf(SessionViewModel session) =>
        [.. session.Session.QueuedMessages.Select(q => new QueuedItemDto(q.Id, q.Text,
            q.Images?.Count ?? 0,
            q.TextFiles is { Count: > 0 } ? [.. q.TextFiles.Select(f => f.Name)] : [],
            q.Defer))];

    public static MessageDto Message(ChatMessageViewModel message, int? seq = null) =>
        new(message.ClientId, message.RunClientId, RoleText(message.Role), message.Text, message.TimeText,
            message.IsError,
            [.. message.ImageFiles],
            message.TextFiles.Count > 0
                ? [.. message.TextFiles.Select(f => new TextFileDto(f.Name, f.SizeText))]
                : null,
            [.. message.ContextMessageIds()], // display↔context 关联:前端凭它按 id 取原始消息显示详情
            message.Role == MessageRole.Assistant ? [.. message.Steps.Select(Step)] : null,
            seq);

    private static string RoleText(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Status => "status",
        MessageRole.Divider => "divider",
        _ => "spinner",
    };

    private static object Step(object raw) => raw switch
    {
        // MsgId/ToolCallId:步骤回链 context 消息的关联 id(前端暂未消费,与快照落盘数据同构)
        ContentSegmentViewModel c => new ContentStep(c.Text, c.MsgId),
        ThinkingSegmentViewModel t => new ThinkingStep(t.Text, t.IsActive, t.Header, t.Seconds, t.MsgId),
        ToolCallViewModel t => new ToolStep(t.DisplayName, t.ToolName, t.Summary, t.DetailBody, t.HasDetail,
            t.IsRunning, t.IsError, t.ResultSummary, t.PathLink, t.StatAdd, t.StatDel,
            [.. t.DiffLines.Select(d => new DiffLineDto(d.LineNo, d.Text, d.Kind))], [.. t.ImageFiles],
            t.SubRunId, t.SubRunTitle, t.ToolCallId),
        AuthRequestViewModel a => new AuthStep(a.WebAuthId, a.Detail, a.Reason, a.Command, [.. a.TrustDirs], a.Decided, a.Granted),
        _ => new UnknownStep(raw.GetType().Name),
    };
}

public sealed record SessionStateDto(
    string SessionId, string Title, bool IsStreaming, bool IsCompressing, bool HasError,
    string Provider, string Model, string ThinkingEffort,
    string WorkingDirectory, string WorkingDirectoryAccess, bool IsWorkDirLocked,
    int? LastPromptTokens, string LastMessageTimeText, int ContextLimit,
    SessionUsageDto Usage,
    int? ChatBgOpacity, string? ChatBgStretch,
    int? WindowBgOpacity, string? WindowBgStretch, List<MessageDto> Messages,
    List<QueuedItemDto> QueuedMessages,
    /// <summary>历史分页:非临时消息行总数(窗口/全量口径一致)。</summary>
    int MessageTotal = 0,
    /// <summary>已加载窗口首行的 seq(null = 全量加载,无窗口)。</summary>
    int? MessageCursor = null,
    /// <summary>窗口之前是否还有更早的历史行(可向上翻页)。</summary>
    bool HasMoreMessages = false,
    /// <summary>归档只读预览:前端禁用输入/发送/命令,仅回看(旧快照可能缺省)。</summary>
    bool IsArchived = false);

/// <summary>会话累计 LLM 用量(随会话落盘持久化;前端状态栏挂载时以此为初始值,再按 usage 帧增量)。</summary>
public sealed record SessionUsageDto(int PromptTotal, int CachedTotal, int CompletionTotal, double LastTps, double Cost);

/// <summary>中途队列条目(轻量展示字段;输入框上方队列条消费)。</summary>
public sealed record QueuedItemDto(string Id, string Text, int ImageCount, List<string> FileNames, bool Defer);

/// <summary>队列条目完整载荷(取消直接丢弃;重新编辑用它回填输入框:原文 + 图片 data URL + 文本文件全文)。</summary>
public sealed record QueuedEditDto(string Id, string Text, List<string>? Images, List<QueuedEditFileDto>? TextFiles);

/// <summary>队列条目取回的文本文件附件。</summary>
public sealed record QueuedEditFileDto(string Name, string Content);

public sealed record SessionListItemDto(
    string SessionId, string Title, string LastMessageTimeText,
    bool IsStreaming, bool IsCompressing, bool HasError, bool Unread, bool Pinned,
    /// <summary>所属文件夹(null = 根组);旧快照缺省 = null。</summary>
    string? FolderId = null,
    /// <summary>组内槽位序号(与同组文件夹共享一个序号空间,前端按它把会话与文件夹合并排序);缺省 = -1(无槽,排组尾)。</summary>
    long SlotSort = -1);

/// <summary>会话列表文件夹(嵌套):parentId 为 null = 顶级;sort = 列表槽位序号
/// (与父组会话共享一个序号空间,前端按它把会话与文件夹合并排序;缺槽 = -1);
/// color = 预设色板 id(前端 FOLDER_COLORS),'' = 默认不着色。</summary>
public sealed record FolderDto(string Id, string Name, string? ParentId, long Sort, string Color = "");

public sealed record MessageDto(
    string? ClientId, string? RunClientId, string Role, string Text, string TimeText,
    bool IsError,
    List<string> ImageFiles, List<TextFileDto>? TextFiles,
    /// <summary>本气泡对应的 context 原始消息 id 集(助手=该轮全部助手消息,按请求序;用户=其 ClientId):
    /// 前端凭它调 /messages/{id} 取原始内容(用量/模型/思考/工具参数)显示详情。</summary>
    List<string> MsgIds,
    List<object>? Steps,
    /// <summary>会话内稳定序号(与 display 表 seq 同口径;临时行为 null):前端据此做历史分页游标与稳定渲染 key。</summary>
    int? Seq = null);

/// <summary>气泡悬停详情(按 MsgIds 聚合原始消息):生成时间/服务端真实用量/所用模型(逐消息归因,
/// 会话中途换模型不丢);用量全 0 = 该轮未记录(估算不可得)。</summary>
public sealed record MessageSummaryDto(
    DateTime? Time, DateTime? CompletedAt,
    int PromptTokens, int CompletionTokens, int TotalTokens, int CachedTokens, int ReasoningTokens,
    string? Provider, string? Model);

public sealed record TextFileDto(string Name, string SizeText);

/// <summary>步骤流基类:Kind 供前端按类型分发渲染(与 SSE 事件同构)。</summary>
public abstract record StepDto(string Kind);

public sealed record ContentStep(string Text, string? MsgId) : StepDto("content");

public sealed record ThinkingStep(string Text, bool IsActive, string Header, double? Seconds, string? MsgId) : StepDto("thinking");

public sealed record ToolStep(
    string DisplayName, string ToolName, string Summary, string Detail, bool HasDetail,
    bool IsRunning, bool IsError, string ResultSummary, string PathLink, string StatAdd, string StatDel,
    List<DiffLineDto> DiffLines, List<string> ImageFiles, int? SubRunId, string SubRunTitle, string? ToolCallId) : StepDto("tool");

public sealed record DiffLineDto(string LineNo, string Text, string Kind);

public sealed record AuthStep(string? AuthId, string Detail, string? Reason, string? Command, List<string> TrustDirs, bool Decided, bool Granted) : StepDto("auth");

public sealed record UnknownStep(string Name) : StepDto("unknown");

// ───────── M2a:会话管理 / 模型切换 / 斜杠命令 ─────────

public sealed record ArchivedSessionDto(string Id, string Title, DateTime UpdatedAt);

public sealed record ProviderDto(string Name, string DefaultModel, string ReasoningEffort, List<ProviderModelDto> Models);

/// <summary>模型清单中的一项:名称 + 高级设置(该模型可用的思考等级 / 支持的附件媒体类型;空 = 默认全集)。</summary>
public sealed record ProviderModelDto(string Name, int ContextTokens, List<string>? ThinkingLevels, List<string>? MediaTypes,
    double PriceInput, double PriceInputCached, double PriceOutput);

public sealed record CommandDto(string Name, string Description, bool TakesArgument);

/// <summary>斜杠命令执行结果:handled=false 时应作为普通消息发送;restored 供 /undo 回填输入框;
/// newSessionId/forkedId 供前端跳转。</summary>
public sealed record CommandResultDto(bool Handled, string? Feedback, string? Restored, string? NewSessionId, string? ForkedId);

// ───────── 侧边栏:任务清单 / 后台任务 / 直播事件 ─────────

public sealed record TodoDto(int Id, string Content, string Status);

public sealed record TaskDto(int Id, string Title, string Command, string Status, bool IsError, DateTime StartedAt, DateTime? EndedAt);

public sealed record LiveDto(string Kind, string User, string Text, double PriceYuan, string Time);

/// <summary>插件前端数据面快照项(Name=插件名,Data=GetPluginData 返回值,任意可序列化形状)。</summary>
public sealed record PluginDataDto(string Name, object? Data);

/// <summary>声明 sidebar UI 插槽的启用插件(侧边栏单开自定义页签,内容 = DLL 内嵌 ui.* 页面)。</summary>
public sealed record PluginUiDto(string Name, string Slots);

/// <summary>底部状态栏插件项(数据面 status 字符串字段的投影)。</summary>
public sealed record StatusPluginDto(string Name, string Status);

public sealed record SidebarDto(List<TodoDto> Todos, List<TaskDto> Tasks, bool LiveBound, List<LiveDto> Live, List<PluginDataDto> PluginData, List<PluginUiDto> UiPlugins, List<StatusPluginDto> StatusPlugins, BiliConnDto? Bili);

/// <summary>BiliLive 插件连接状态(侧边栏圆点+状态行;State 为小写稳定标识,见 LiveConnStatus.StateKey)。</summary>
public sealed record BiliConnDto(string State, string Host, int HostIndex, int HostCount, int Round,
    string RoomCode, int InternalRoomId, bool RoomLive, string Error, DateTime ChangedAt, DateTime? NextRetryAt, long Received);
