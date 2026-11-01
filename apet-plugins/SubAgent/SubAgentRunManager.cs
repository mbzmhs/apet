namespace APet.Plugins.SubAgent;

using APet.Framework.Models;
using APet.Framework.Session;

/// <summary>子代理运行状态。</summary>
public enum SubAgentStatus
{
    Running,
    Completed, // 正常结束(FinalContent = 最后助手正文)
    Failed,    // 运行出错(Error = 原因)
    Cancelled, // 被 sub_kill / 主对话停止 / 宿主关闭终止
}

/// <summary>
/// 一次子代理运行:独立 CTS(与主轮取消令牌联动)+ 事件帧日志(模态框重放用)。
/// 状态迁移只发生一次(Running → 终态);事件帧在发生顺序上记录,超出上限裁掉最旧。
/// </summary>
public sealed class SubAgentRun
{
    /// <summary>事件帧日志上限:超出裁掉最旧(模态框重放时标注"较早内容已裁")。</summary>
    public const int MaxFrames = 4000;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Action<string, string, object?>? _sink;

    public int Id { get; }
    public string Name { get; }
    public string Prompt { get; }

    /// <summary>任务名称(sub_run 的 title 参数;展示在聊天工具行/侧边栏,替代"运行 #N"式模糊标题)。</summary>
    public string Title { get; }
    public DateTime StartedAt { get; }
    public DateTime? EndedAt { get; private set; }
    public SubAgentStatus Status { get; private set; } = SubAgentStatus.Running;

    /// <summary>发起本运行的主会话 Id(子代理运行与 session 绑定,侧边栏按会话过滤展示)。</summary>
    public string? SessionId { get; }

    /// <summary>子代理最后助手正文(sub_wait 返回内容;终态后有效)。</summary>
    public string? FinalContent { get; private set; }

    /// <summary>运行错误原因(Failed 时有效)。</summary>
    public string? Error { get; private set; }

    /// <summary>状态迁移到终态后触发一次;可能在任意线程。</summary>
    public event Action? StateChanged;

    /// <summary>本运行的取消令牌(子代理 LLM/工具随其中断)。</summary>
    public CancellationToken Token => _cts.Token;

    // ───────── 运行中传话(sub_tell) ─────────
    // 状态机:入队(_pendingTell)→ 运行循环收编进请求(_tellInFlight)→ 下一条非空助手正文 = 回复;
    // 运行在回复前终止:已收编的以最终答案作为回复,未收编的视为未处理(null)。

    private readonly object _tellGate = new();
    private AgentSession? _liveSession; // 运行期间的活会话(LaunchAsync 登记/收尾解除);传话投递目标
    private string? _pendingTellId; // 已入队、尚未被运行循环收编的传话
    private bool _tellInFlight; // 传话已收编进当前请求,等待下一条非空助手正文
    private TaskCompletionSource<string?>? _replyTcs; // 传话回复等待者;结果 null = 运行在回复前结束

    /// <summary>正在运行且已登记会话时为 true;传话投递与等待都需要。</summary>
    internal bool CanAcceptTell
    {
        get
        {
            lock (_tellGate)
                return Status == SubAgentStatus.Running && _liveSession is not null;
        }
    }

    internal void AttachSession(AgentSession session)
    {
        lock (_tellGate)
        {
            _liveSession = session;
            session.QueueDrained += OnQueueDrained;
        }
    }

    internal void DetachSession(AgentSession session)
    {
        lock (_tellGate)
        {
            if (ReferenceEquals(_liveSession, session))
                _liveSession = null;
            session.QueueDrained -= OnQueueDrained;
        }
        ResolveTellOnEnd(null);
    }

    /// <summary>
    /// 投递一条传话到子代理会话队列(下一个请求前被收编,子代理当作用户消息继续工作)。
    /// 返回等待回复的任务(结果 = 子代理回复正文;null = 运行在回复前结束);运行不在运行态/未就绪/已有传话在途时返回 null 并给出原因。
    /// </summary>
    internal Task<string?>? EnqueueTell(string text, out string error)
    {
        lock (_tellGate)
        {
            if (Status != SubAgentStatus.Running)
            {
                error = $"Subagent run #{Id} has already finished ({Status.ToString().ToLowerInvariant()}). " +
                        "Mid-run instructions require a running subagent; start a new one with sub_run (mention its conclusions so far) if you need to continue.";
                return null;
            }
            if (_liveSession is null)
            {
                error = "Subagent session is not ready yet; retry in a moment.";
                return null;
            }
            if (_pendingTellId is not null || _replyTcs is not null)
            {
                error = "A previous sub_tell is still awaiting its reply; wait for it before sending another.";
                return null;
            }
            var item = new QueuedUserMessage { Text = text };
            _pendingTellId = item.Id;
            _replyTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _liveSession.Enqueue(item);
            Publish("tell", new { text });
            error = "";
            return _replyTcs.Task;
        }
    }

    /// <summary>运行循环收编队列时回调(session.QueueDrained):传话进入在途请求。</summary>
    private void OnQueueDrained(IReadOnlyList<QueuedUserMessage> items, bool atRunStart)
    {
        lock (_tellGate)
        {
            if (_pendingTellId is not null && items.Any(i => string.Equals(i.Id, _pendingTellId, StringComparison.Ordinal)))
            {
                _pendingTellId = null;
                _tellInFlight = true;
            }
        }
    }

    /// <summary>每条助手消息组装完成时回调(SubAgentCallbacks 转发):在途传话的下一条非空正文即回复。</summary>
    internal void OnAssistantMessage(ChatMessage message)
    {
        TaskCompletionSource<string?>? tcs = null;
        string? reply = null;
        lock (_tellGate)
        {
            if (_tellInFlight && _replyTcs is not null && !string.IsNullOrWhiteSpace(message.Content))
            {
                _tellInFlight = false;
                reply = message.Content;
                tcs = _replyTcs;
                _replyTcs = null;
            }
        }
        tcs?.TrySetResult(reply);
    }

    /// <summary>运行终止/会话解除时收口等待者:已收编的传话以最终答案(可能为 null)作为回复,未收编的视为未处理。</summary>
    private void ResolveTellOnEnd(string? finalContent)
    {
        TaskCompletionSource<string?>? tcs = null;
        string? reply = null;
        lock (_tellGate)
        {
            if (_replyTcs is not null)
            {
                reply = _tellInFlight ? finalContent : null;
                tcs = _replyTcs;
                _replyTcs = null;
                _tellInFlight = false;
                _pendingTellId = null;
            }
        }
        tcs?.TrySetResult(reply);
    }

    internal SubAgentRun(int id, string name, string prompt, string title, string? sessionId, Action<string, string, object?>? sink)
    {
        Id = id;
        Name = name;
        Prompt = prompt;
        Title = title;
        SessionId = sessionId;
        StartedAt = DateTime.Now;
        _sink = sink;
    }

    /// <summary>记录一帧事件并转发给宿主(事件总线);任何线程可调。</summary>
    internal void Publish(string type, object? data)
    {
        lock (_gate)
        {
            _frames.Add((type, data));
            if (_frames.Count > MaxFrames)
            {
                _frames.RemoveRange(0, _frames.Count - MaxFrames);
                FramesTruncated = true;
            }
        }
        _sink?.Invoke(Id.ToString(), type, data);
    }

    private readonly List<(string Type, object? Data)> _frames = new();

    /// <summary>
    /// 事件帧日志快照(类型 + 载荷,发生顺序);超上限后仅存最近的,并置 FramesTruncated。
    /// 每次返回加锁拷贝:写侧 Publish 在任意线程追加/裁剪,直接暴露底层 List 会让宿主读取
    /// (Web 快照序列化)与写侧并发读写同一容器;宿主读取点无需自行加锁。
    /// </summary>
    public List<(string Type, object? Data)> Frames
    {
        get { lock (_gate) return new List<(string Type, object? Data)>(_frames); }
    }

    /// <summary>日志是否被裁过(模态框重放提示用)。</summary>
    public bool FramesTruncated { get; private set; }

    /// <summary>已发出 compression_started 但尚未收到 compression_finished(摘要失败/取消时插件不发完成事件)。</summary>
    private bool _compressionOpen;

    /// <summary>运行进入终态时收口未完成的压缩:补发 compression_closed,前端移除齿轮行。
    /// 与主聊天"中断/失败不留痕迹"同规格(主聊天由宿主 ViewModel 兜底,帧日志链路在此兜底)。</summary>
    internal void SettleCompression()
    {
        lock (_gate)
        {
            if (!_compressionOpen)
                return;
            _compressionOpen = false;
        }
        Publish("compression_closed", null);
    }

    internal void MarkCompressionOpen(bool open)
    {
        lock (_gate)
            _compressionOpen = open;
    }

    internal void Complete(string content) => Transition(SubAgentStatus.Completed, content, null);

    internal void Fail(string error) => Transition(SubAgentStatus.Failed, null, error);

    /// <summary>终止运行(取消令牌使 LLM/工具立即中断);已结束的运行返回 false。</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (Status != SubAgentStatus.Running)
                return false;
        }
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 运行收尾中:状态迁移兜底 */ }
        Transition(SubAgentStatus.Cancelled, null, null);
        return true;
    }

    private void Transition(SubAgentStatus to, string? content, string? error)
    {
        bool raised = false;
        lock (_gate)
        {
            if (Status != SubAgentStatus.Running)
                return;
            Status = to;
            EndedAt = DateTime.Now;
            FinalContent = content;
            Error = error;
            raised = true;
        }
        if (raised)
        {
            SettleCompression(); // 任何终态(完成/失败/取消)都不让齿轮行残留
            ResolveTellOnEnd(FinalContent); // 在途传话以最终答案收口;未收编的传话告知等待方"未处理"
            StateChanged?.Invoke();
        }
    }
}

/// <summary>
/// 子代理运行管理器:单 Agent 一份,内存态(进程不随应用重启存活,故不持久化)。
/// sub_run 启动(并发上限内)、sub_kill 终止;Changed 供侧边栏刷新;
/// 历史保留上限内按最早结束淘汰(Running 永不淘汰)。
/// </summary>
public sealed class SubAgentRunManager
{
    /// <summary>单 Agent 并发子代理上限(超出时 sub_run 报错,等结束或 sub_kill)。</summary>
    public const int MaxConcurrentRuns = 4;

    /// <summary>保留的已结束运行上限:超出后从最早结束的开始淘汰(Running 永不淘汰)。</summary>
    public const int MaxKeptRuns = 40;

    private readonly object _gate = new();
    private readonly List<SubAgentRun> _runs = new();
    private int _nextId = 1;

    /// <summary>运行列表或状态变化(新增/结束/被杀)后触发;可能在任意线程。</summary>
    public event Action? Changed;

    /// <summary>是否还能再启动一个子代理(并发未超限)。仅作快速预判;权威判定在 Start 的登记锁内。</summary>
    public bool CanStart()
    {
        lock (_gate)
            return _runs.Count(r => r.Status == SubAgentStatus.Running) < MaxConcurrentRuns;
    }

    /// <summary>登记一次新运行;已达并发上限时返回 null。上限检查与登记在同一把锁内:
    /// 此前工具侧先 CanStart 再 Start,两次加锁之间存在窗口,并发 sub_run 可同时通过预检突破 4 上限。</summary>
    internal SubAgentRun? Start(string name, string prompt, string title, string? sessionId, Action<string, string, object?>? sink)
    {
        SubAgentRun? run;
        lock (_gate)
        {
            if (_runs.Count(r => r.Status == SubAgentStatus.Running) >= MaxConcurrentRuns)
                return null;
            run = new SubAgentRun(_nextId++, name, prompt, title, sessionId, sink);
            _runs.Add(run);
            // 淘汰最早结束的已结束运行,列表不无限增长
            while (_runs.Count(r => r.Status != SubAgentStatus.Running) > MaxKeptRuns)
            {
                var oldest = _runs.Where(r => r.Status != SubAgentStatus.Running)
                    .OrderBy(r => r.EndedAt ?? r.StartedAt).FirstOrDefault();
                if (oldest is null)
                    break;
                _runs.Remove(oldest);
            }
        }
        run.StateChanged += () => Changed?.Invoke();
        Changed?.Invoke();
        return run;
    }

    /// <summary>按 id 查找运行;sessionId 非空时仅匹配该主会话发起的运行(跨对话不可见)。</summary>
    public SubAgentRun? Find(int id, string? sessionId = null)
    {
        lock (_gate)
            return _runs.FirstOrDefault(r => r.Id == id
                && (sessionId is not { Length: > 0 } || string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)));
    }

    /// <summary>运行快照(新运行在前);sessionId 非空时只返回该主会话发起的运行(子代理与 session 绑定)。</summary>
    public IReadOnlyList<SubAgentRun> Snapshot(string? sessionId = null)
    {
        lock (_gate)
        {
            var q = _runs.AsEnumerable().Reverse().AsEnumerable();
            if (sessionId is { Length: > 0 })
                q = q.Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal));
            return q.ToList();
        }
    }

    /// <summary>从列表移除一个已结束的运行(运行中不可移除,需先 sub_kill);供侧边栏"清除"。返回是否移除成功。</summary>
    public bool Remove(int id, string? sessionId = null)
    {
        bool removed;
        lock (_gate)
        {
            var run = _runs.FirstOrDefault(r => r.Id == id
                && (sessionId is not { Length: > 0 } || string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)));
            removed = run is not null && run.Status != SubAgentStatus.Running;
            if (removed)
                _runs.Remove(run);
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>正在运行的子代理数(侧边栏角标用)。</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
                return _runs.Count(r => r.Status == SubAgentStatus.Running);
        }
    }
}
