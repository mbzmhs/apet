using APet.Framework.LLM;
using APet.Framework.Models;

namespace APet.Framework.Session;

/// <summary>
/// 一次对话会话:持有上下文消息与 LLM 连接实例。
/// Agent 无状态,每次调用传入 Session;运行完成后的新消息由 Agent 写回。
/// </summary>
public sealed class AgentSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>本会话专用的 LLM 连接实例(Agent 配置变更时可热更换,下一个请求即用新连接)。</summary>
    public ILlmClient LlmClient { get; private set; }

    private ILlmClient? _pendingClient;

    /// <summary>更换 LLM 连接实例。轮次进行中(IsRunning)不立即生效:挂起到 EndRun 再切换,
    /// 保证当前轮的后续迭代(工具轮内的下一次请求)仍使用发起时的供应商/模型。</summary>
    public void UpdateLlmClient(ILlmClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_runGate)
        {
            if (IsRunning)
            {
                _pendingClient = client; // 轮内不换连接:本轮结束后生效
                return;
            }
            LlmClient = client;
        }
    }

    /// <summary>当前会话的完整上下文消息。</summary>
    public List<ChatMessage> Messages { get; } = new();

    /// <summary>最近一次请求供应商回报的真实 prompt tokens(含 system 与工具定义;0 = 尚无回报)。
    /// 本地估算通常偏低,压缩触发等需要真实占用的场景以此为基准。</summary>
    public long LastPromptTokens { get; internal set; }

    /// <summary>重启后由宿主从会话文件还原服务端回报的真实占用:新进程内存值归零后,
    /// 开跑时的压缩触发检查仍能按真实占用决策(首次新请求回报后即被覆盖)。</summary>
    public void RestoreLastPromptTokens(long tokens) => LastPromptTokens = Math.Max(0, tokens);

    public AgentSession(ILlmClient llmClient)
    {
        LlmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
    }

    // ───────── 请求锁与会话级取消 ─────────

    private readonly object _runGate = new();
    private CancellationTokenSource? _runCts;

    /// <summary>是否有请求正在执行(请求锁)。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 开始一次请求(请求锁):同一会话存在未完成的请求时抛出 InvalidOperationException。
    /// 返回本次请求的取消令牌(与会话取消、外部取消联动);取消即可快速释放 HTTP 连接。
    /// </summary>
    public CancellationToken BeginRun(CancellationToken external)
    {
        lock (_runGate)
        {
            if (IsRunning)
                throw new InvalidOperationException("This session already has a request in flight.");
            IsRunning = true;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(external);
            return _runCts.Token;
        }
    }

    /// <summary>请求结束(成功/失败/取消),释放请求锁;轮次期间挂起的新连接此时切换生效。</summary>
    public void EndRun()
    {
        lock (_runGate)
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            if (_pendingClient is { } pending)
            {
                LlmClient = pending; // 轮次期间换的供应商/模型,从下一轮开始生效
                _pendingClient = null;
            }
        }
    }

    /// <summary>中断当前请求:取消令牌使 HTTP 连接立即释放并返回中断。</summary>
    public void CancelRun()
    {
        lock (_runGate)
            _runCts?.Cancel();
    }

    /// <summary>写入一条消息;Token 占用在对话时记录(未记录则估算),供压缩等按条累计。</summary>
    public void AddMessage(ChatMessage message)
    {
        if (message.TokenCount <= 0)
            message.TokenCount = TokenEstimator.EstimateMessage(message);
        Messages.Add(message);
    }

    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
            if (message.TokenCount <= 0)
                message.TokenCount = TokenEstimator.EstimateMessage(message);
        Messages.AddRange(messages);
    }

    /// <summary>上下文压缩写回:把最前 count 条旧消息替换为 replacement(summarize → 摘要消息;truncate → null 即直接删除),其后消息不变。</summary>
    public void ReplacePrefix(int count, ChatMessage? replacement)
    {
        if (count <= 0) return;
        if (count > Messages.Count)
            count = Messages.Count; // 运行中压缩:边界可能落在尚未写回会话的消息上,只移除会话中实际存在的部分
        Messages.RemoveRange(0, count);
        if (replacement is not null)
        {
            if (replacement.TokenCount <= 0)
                replacement.TokenCount = TokenEstimator.EstimateMessage(replacement);
            Messages.Insert(0, replacement);
        }
    }

    // ───────── 中途消息队列(运行中插话) ─────────

    private readonly object _queueGate = new();
    private readonly List<QueuedUserMessage> _queue = new();

    /// <summary>队列快照(FIFO 顺序,供界面展示与开跑前显式派发取用)。</summary>
    public IReadOnlyList<QueuedUserMessage> QueuedMessages
    {
        get { lock (_queueGate) return _queue.ToList(); }
    }

    /// <summary>队列变化(入队/移除;被请求消费走 QueueDrained)。宿主订阅后向前端广播刷新队列条。</summary>
    public event Action? QueueChanged;

    /// <summary>队列被某次请求消费:Agent 完成拼装后触发,宿主据此补用户气泡并刷新队列条。
    /// atRunStart=true 表示开跑组装时消费(条目早于本轮输入到达,插在输入之前);
    /// false 表示开跑后消费(首轮/工具轮,条目追加在本轮待发内容之尾)。</summary>
    public event Action<IReadOnlyList<QueuedUserMessage>, bool>? QueueDrained;

    /// <summary>入队一条中途插话(运行中发送的消息先进这里,下一次请求发出前按 FIFO 带出)。</summary>
    public void Enqueue(QueuedUserMessage item)
    {
        lock (_queueGate) _queue.Add(item);
        QueueChanged?.Invoke();
    }

    /// <summary>取出并移除一条队列项:取消 = 丢弃返回值;重新编辑 = 拿返回的原文与附件回填输入框。</summary>
    public QueuedUserMessage? RemoveQueued(string id)
    {
        QueuedUserMessage? removed;
        lock (_queueGate)
        {
            removed = _queue.Find(q => q.Id == id);
            if (removed is not null)
                _queue.Remove(removed);
        }
        if (removed is not null)
            QueueChanged?.Invoke();
        return removed;
    }

    /// <summary>清空并返回全部队列项(FIFO)。消费通知由 Agent 拼装完成后统一触发(QueueDrained)。
    /// stopAtDeferred=true 只取队首连续"可立即拼入"的普通消息,碰到命令样或标记"本轮后发送"的条目
    /// 即停 —— 命令不能进 LLM 上下文、延迟消息不拼进进行中的请求,都留给空闲态的派发处理;
    /// 队首即被挡条目时返回空列表。</summary>
    public List<QueuedUserMessage> DrainQueued(bool stopAtDeferred = false)
    {
        lock (_queueGate)
        {
            if (_queue.Count == 0)
                return [];
            var take = 0;
            while (take < _queue.Count && !(stopAtDeferred && (_queue[take].IsCommandLike || _queue[take].Defer)))
                take++;
            if (take == 0)
                return [];
            var batch = _queue.GetRange(0, take);
            _queue.RemoveRange(0, take);
            return batch;
        }
    }

    /// <summary>清空全部队列项(轮次被手动终止/出错终止:未及拼入的插话一并丢弃),返回被清掉的条目;
    /// 经 QueueChanged 广播刷新队列条。队列为空时静默返回空列表。</summary>
    public List<QueuedUserMessage> ClearQueue()
    {
        List<QueuedUserMessage> items;
        lock (_queueGate)
        {
            items = _queue.ToList();
            _queue.Clear();
        }
        if (items.Count > 0)
            QueueChanged?.Invoke();
        return items;
    }

    /// <summary>批量回队并插到队首(单次 QueueChanged):宿主拆批派发时把"命令及之后的存量"放回,
    /// 插队首保证相对顺序 —— 排空到回填之间新到的插话仍排在它们后面(FIFO 不乱)。</summary>
    public void RequeueFront(IReadOnlyList<QueuedUserMessage> items)
    {
        lock (_queueGate)
        {
            if (items.Count == 0)
                return;
            _queue.InsertRange(0, items);
        }
        QueueChanged?.Invoke();
    }

    /// <summary>切换某条队列项的"本轮后发送"标记(队列条按钮);不存在返回 false。
    /// 经 QueueChanged 广播,前端队列条随全量快照刷新。</summary>
    public bool SetQueuedDefer(string id, bool defer)
    {
        lock (_queueGate)
        {
            var item = _queue.Find(q => q.Id == id);
            if (item is null)
                return false;
            if (item.Defer == defer)
                return true;
            item.Defer = defer;
        }
        QueueChanged?.Invoke();
        return true;
    }

    /// <summary>把某条队列项移到新位置(队列条拖拽把手排序);不存在返回 false。
    /// index = 移除自身后的插入位置(0 = 队首,count-1 = 队尾,越界夹取);经 QueueChanged 广播全量新序。</summary>
    public bool MoveQueued(string id, int index)
    {
        lock (_queueGate)
        {
            var from = _queue.FindIndex(q => q.Id == id);
            if (from < 0)
                return false;
            var item = _queue[from];
            _queue.RemoveAt(from);
            _queue.Insert(Math.Clamp(index, 0, _queue.Count), item);
        }
        QueueChanged?.Invoke();
        return true;
    }

    /// <summary>由 Agent 在完成拼装后触发 QueueDrained(事件只能由声明类型触发)。</summary>
    public void RaiseQueueDrained(IReadOnlyList<QueuedUserMessage> items, bool atRunStart) =>
        QueueDrained?.Invoke(items, atRunStart);
}

/// <summary>中途消息队列条目:运行中用户的插话,随下一次请求按 FIFO 拼进上下文(不打断当前轮)。</summary>
public sealed class QueuedUserMessage
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = "";

    /// <summary>图片/媒体 data URL 列表(与普通发送同款多模态输入)。</summary>
    public List<string>? Images { get; set; }

    /// <summary>文本文件附件:重新编辑时原样回填输入框;进入请求时每文件拼为独立 text 片段。</summary>
    public List<QueuedTextFile>? TextFiles { get; set; }

    public DateTime TimeCreated { get; } = DateTime.Now;

    /// <summary>命令样条目(与前端空闲态判定同规则:去空白后首字符是 / 或 $ 且无附件)。
    /// 命令按命令触发、不进 LLM 上下文:Agent 两处排空点见到它即停,派发在空闲态把它交给命令核心。</summary>
    public bool IsCommandLike
    {
        get
        {
            var t = Text.TrimStart();
            if (t.Length == 0 || t[0] is not ('/' or '$'))
                return false;
            return Images is not { Count: > 0 } && TextFiles is not { Count: > 0 };
        }
    }

    /// <summary>本轮后发送(用户经 Ctrl+Enter 入队或队列条按钮标记):不拼进进行中的请求,
    /// 排空点同命令一样跳过,等当前轮成功结束后由派发作为新一轮普通消息发出。</summary>
    public bool Defer { get; set; }
}

/// <summary>队列条目携带的文本文件附件。</summary>
public sealed record QueuedTextFile(string Name, string Content);
