using APet.Framework.Plugin;
using APet.Framework.Session;
using APet.Framework.Tools;

namespace APet.Plugins.Memory;

/// <summary>
/// 记忆插件:单 Agent 的长期记忆(SQLite,全部会话共享),由宿主注册 mem_record / mem_recent /
/// mem_range / mem_use / mem_edit / mem_search 六个工具。本类负责设置(自动遗忘)与返回预算:
/// 查询工具先取回全部命中,再按"剩余上下文且尽量不越过压缩阈值线"的 Token 预算从旧到新截断。
/// </summary>
public sealed class MemoryPlugin : AgentPluginBase
{
    /// <summary>单次返回至多占最大上下文的 x%(预算上限,避免把整库记忆灌进新会话)。</summary>
    public const int MaxBudgetPercent = 20;

    /// <summary>上下文上限未知(解析不到有效值)时的回退预算(tokens)。</summary>
    public const int FallbackBudgetTokens = 2048;

    [PluginSetting("forgetAfterDays", Description = "自动忘记 N 天未使用过的记忆(0=永不忘记)", Min = 0, Max = 3650)]
    public int ForgetAfterDays { get; set; }

    /// <summary>生效的上下文 Token 上限(与压缩同源,按会话各自的模型设定;参数 = ToolExecutionScope.SessionId);宿主接线。</summary>
    public Func<string, int>? ContextLimit { get; set; }

    /// <summary>压缩触发阈值百分比(10-95,宿主从 AgentConfig 实时读取):返回记忆后总用量尽量不超过该线。</summary>
    public Func<int>? CompressThreshold { get; set; }

    /// <summary>是否还有任一 mem_* 工具处于启用状态(宿主注入);全部禁用时不注入记忆行为指引,避免模型调用不存在的工具。</summary>
    public Func<bool>? ToolsAvailable { get; set; }

    public MemoryStore Store { get; }

    public MemoryPlugin(string agentId, string agentsRoot) => Store = new MemoryStore(agentId, agentsRoot);

    public override string Name => "memory";
    public override void Initialize() { } // 存储懒打开,无需启动动作
    public override void Dispose() => Store.Dispose();

    /// <summary>
    /// 记忆行为指引(注入 system prompt):核心是"涉及可能记过的事先查记忆,不要猜、不要让用户重复",
    /// 以及记录/标记使用的时机。全部 mem_* 工具被禁用时返回 null 不注入。
    /// </summary>
    public override string? GetSystemPromptSegment(AgentSession session)
    {
        if (ToolsAvailable is { } check && !check())
            return null;
        return """
            You have a long-term memory shared across all your sessions (mem_* tools). Use it proactively:
            - Before answering about personal facts, preferences, past events, decisions, names, dates, or anything the user may have told you before — check memory FIRST (mem_search with several broad alternative keywords, or mem_recent for recent context). Never guess, and never ask the user to repeat something they already told you.
            - Save durable information with mem_record when the user asks you to remember something, or when important personal context emerges.
            - When a stored memory turns out wrong or outdated (the user corrects you, or circumstances changed), fix it with mem_edit; pass an empty content to forget it.
            - When you actually apply a recalled memory in your reply, mark it with mem_use so it survives auto-forget.
            - If memory has no record of something, say so plainly; never invent remembered details.
            """;
    }

    private long _lastSweepTicks; // 上次清扫时刻(Environment.TickCount64)

    /// <summary>自动遗忘清扫:每个记忆工具调用前执行;ForgetAfterDays=0 时不删。
    /// 60s 节流:mem_* 每次调用都全表 DELETE 会让高频对话(如 mem_search 连发)反复全表扫描,
    /// 过期判断按"天"计,分钟级精度毫无损失。</summary>
    public void ForgetSweep()
    {
        if (ForgetAfterDays <= 0)
            return;
        var now = Environment.TickCount64;
        var last = System.Threading.Interlocked.Read(ref _lastSweepTicks);
        if (now - last < 60_000)
            return;
        if (System.Threading.Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last)
            return; // 并发调用方已触发本轮清扫
        Store.Forget(ForgetAfterDays);
    }

    /// <summary>
    /// 计算本次返回的 Token 预算:min(距硬上限剩余, 距压缩阈值线剩余, 上限×MaxBudgetPercent%)。
    /// currentUsage 为工作消息实时用量(Agent 在工具执行时经 ToolExecutionScope 提供);null=未知按 0 计。
    /// 已越过阈值线时预算为 0(调用方应提示模型收窄查询而不是继续灌入)。
    /// </summary>
    public int ComputeBudget(Func<int>? currentUsage)
    {
        var limit = ContextLimit?.Invoke(ToolExecutionScope.SessionId ?? "") ?? 0;
        if (limit <= 0)
            return FallbackBudgetTokens;
        var current = currentUsage?.Invoke() ?? 0;
        var thresholdPct = Math.Clamp(CompressThreshold?.Invoke() ?? 80, 10, 95);
        var hardRemaining = limit - current;
        var softRemaining = (long)limit * thresholdPct / 100 - current; // 可为负:已越过阈值线
        var budget = (int)Math.Min(hardRemaining, Math.Max(softRemaining, 0));
        budget = Math.Min(budget, limit * MaxBudgetPercent / 100);
        return Math.Max(budget, 0);
    }
}
