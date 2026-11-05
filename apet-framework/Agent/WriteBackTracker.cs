using APet.Framework.Models;

namespace APet.Framework.Agent;

/// <summary>
/// 运行循环的增量写回状态机(按引用追踪,不依赖 working 列表下标)。
/// 背景下标方案的两个失效模式:运行中压缩(OnBeforeRequest/OnAfterTurn)会 Clear+AddRange 重排
/// working 列表,旧 writeIndex 落在新布局上——①界内时哨兵取到的是重排后的新对象,IndexOf 自匹配,
/// writeIndex 原地不动跳过区间;②越界时重定位整体跳过,completedIndex 比较恒假,每轮零写回。
/// 改为按消息对象引用记账:待写回集合独立于 working 存续,压缩怎么重排都不影响写回完整性;
/// 队列用户消息即使被极端压缩挤出 working(保留窗口不够长),也照样落历史,不静默丢失。
/// </summary>
public sealed class WriteBackTracker
{
    private readonly Action<IReadOnlyList<ChatMessage>> _write;
    private readonly List<ChatMessage> _pending = new(); // 已完成待写回(按引用,保序)
    private readonly List<ChatMessage> _flushed = new();

    public WriteBackTracker(Action<IReadOnlyList<ChatMessage>> write)
    {
        _write = write;
    }

    /// <summary>累计写回的全部消息(供 AgentResult.NewMessages)。</summary>
    public IReadOnlyList<ChatMessage> Flushed => _flushed;

    /// <summary>写回基线:输入头部(先于输入的队列插话 + 输入本身)立即落库;
    /// 输入被插件从 working 移除时整段不写(避免把保留窗口重复写进历史)。</summary>
    public void CaptureInput(List<ChatMessage> working, ChatMessage input, ChatMessage? firstQueued)
    {
        var inputIdx = working.IndexOf(input);
        if (inputIdx < 0)
            return; // 输入被插件移除:不写回
        var headStart = firstQueued is not null ? working.IndexOf(firstQueued) : inputIdx;
        if (headStart < 0 || headStart > inputIdx)
            headStart = inputIdx;
        _pending.AddRange(working.GetRange(headStart, inputIdx - headStart + 1));
    }

    /// <summary>标记一批已完成的消息(队列插话收编 / 一轮"助手+工具结果"成对完成)。
    /// 只允许成对完整地进入:未完成的助手或孤儿工具结果不得加入,中断路径由此保证不写孤儿。</summary>
    public void MarkCompleted(IEnumerable<ChatMessage> messages) => _pending.AddRange(messages);

    /// <summary>把已完成待写回的消息落库:按引用写,与 working 当前布局无关。</summary>
    public void Flush()
    {
        if (_pending.Count == 0)
            return;
        _write(_pending);
        _flushed.AddRange(_pending);
        _pending.Clear();
    }
}
