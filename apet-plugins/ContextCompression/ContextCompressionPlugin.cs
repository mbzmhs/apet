using System.Runtime.CompilerServices;
using System.Text;
using APet.Framework.Agent;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Session;
using AgentClass = APet.Framework.Agent.Agent;

namespace APet.Plugins.ContextCompression;

// ───────── 上下文压缩插件 ─────────

/// <summary>
/// 上下文压缩(参考 opencode 的 compact):估算上下文 Token 达到上限阈值时处理保留窗口之外的旧消息;
/// 保留窗口为"最近 x% tokens 的整条消息"(从最新消息往前累计每条记录的 token 直到达到百分比)。
/// 策略 summarize:让 AI 总结旧消息,摘要长度用 max_tokens 限制为上限的 y%(并在模板前明确告知字数预算),
/// 生成全新上下文,摘要置于消息头部(紧随 system、早于保留的近期消息);重复压缩时从上下文的头部摘要消息
/// 携带上次摘要,不丢信息(摘要随会话落盘,重启后仍可续接)。
/// 总结请求优先走前缀缓存路径:真实上下文原样保留到保留窗口边界、压缩模板追加为末尾用户消息、
/// 工具定义与正常轮次一致——旧上下文整段命中 KV 缓存,只有模板按全价计费;失败时回退合成转录稿的独立请求。
/// 策略 truncate:保留窗口之外直接丢弃,不调用 LLM、不生成摘要(已有的旧摘要消息保留)。
/// summarize 时把 system prompt(角色设定/身份)一并传给总结器:这类信息只存在于 system prompt,
/// 不在对话消息里;不传的话压缩器只能从对话猜身份,摘要会写成"未具名",重复压缩后持续丢失。
/// 压缩结果同步写回会话历史(summarize → 旧消息换成摘要消息;truncate → 旧消息直接删除):
/// 重启后载入的上下文即关闭前的压缩态;界面 Display 气泡不受影响,仍是全部聊天记录。
/// 压缩运行在运行请求锁内:进行中的会话拒绝新的发送(宿主经开始/结束事件显示状态行)。
/// </summary>
public sealed class ContextCompressionPlugin : AgentPluginBase
{
    /// <summary>最近一次压缩总结的真实用量。RunAsync 内经 ReportUsageAsync 进回调链;
    /// 空闲手动压缩(无活动回调链)由宿主消费此值补记会话统计,消费后清零。</summary>
    public APet.Framework.LLM.LlmUsage? LastCompressionUsage { get; set; }
    private const int MaxTranscriptChars = 24_000;

    /// <summary>传给总结器的 system prompt 字符上限(防御异常长设定;正常角色设定远低于此)。</summary>
    private const int MaxSystemPromptChars = 12_000;

    /// <summary>摘要模板缺省值(设置页清空时回退;即"轻量通用"预设)。</summary>
    public const string DefaultTemplate =
        "You are compacting a conversation for an AI assistant. Summarize it concisely but completely,"
        + " in the same language as the conversation. Cover: the user's requests and constraints, key decisions,"
        + " file paths and important tool results, and any pending tasks. Output only the summary text.";

    /// <summary>系统预设摘要模板(场景 → 模板正文):设置页组合框可选,选中后填入文本框仍可手动微调。
    /// 结构固定英文小节标题、正文随对话语言;长度预算由引擎自动前置,模板内不写。</summary>
    public static readonly (string Name, string Text)[] Presets =
    {
        ("轻量通用", DefaultTemplate),
        ("编程开发",
            @"You are compacting a software-development conversation for an AI coding assistant. Summarize it (including any [Previous summary]) into a structured memory summary. Write the content in the same language as the conversation; keep the section headings exactly in English. Output exactly these sections, keeping every one (write ""(none)"" when empty):

## Objective
- The task currently in flight: what is being built or fixed, acceptance criteria
- If work was completed earlier in the same session, list it as background context (prefixed with ""Recently completed in same session"")

## Important Details
- Project context: name, working directory, branch, remote, deploy target, key endpoints (verbatim)
- Hard constraints: commit conventions, files/SDKs not to touch, encoding or format restrictions, deploy procedures
- Environment gotchas (hard-won lessons from this session): OS/tool quirks, failures that were worked around and why — these are the highest-value facts for resuming work later, do not drop them

## Work State
### Completed
- Each shipped item as one bullet: what, root cause → fix for non-trivial fixes, commit hash if one exists
### Active
- The in-progress task: diagnosed root cause (if any), the working plan, open design decisions to make
### Blocked
- What is blocking progress and why; ""(none)"" if unblocked

## Next Move
- Numbered concrete steps in execution order; include how each step is verified (how to confirm it worked), and any user verification to request at the end

## Relevant Files
- `path` — role: key functions/classes/endpoints with line numbers where known, and why it matters to the active task

## Recent Commits
- `hash` message — status (pushed / deployed / verified), newest first

Rules: terse bullets, not prose; preserve exact file paths, commands, identifiers and error messages; output only the summary itself.
If a [Previous summary] is provided: carry forward still-relevant facts from it; where old and new conflict, the newer wins."),
        ("文章写作",
            @"You are compacting a writing conversation for an AI writing assistant. Summarize it (including any [Previous summary]) into a structured memory summary. Write the content in the same language as the conversation; keep the section headings exactly in English. Output exactly these sections, keeping every one (write ""(none)"" when empty):

## Topic & Requirements
- subject, purpose, audience, length/format/style requirements (verbatim where quoted)
## Outline & Structure
- agreed structure and section order; what each part covers
## Key Content
- essential facts, quotes, data points and phrasing the user asked to keep (preserve exact wording)
## Feedback & Revisions
- revision requests and which were applied; rejected directions (do not repeat them)
## Status & Next Steps
- current draft state, remaining sections, pending tasks

Rules: terse bullets, not prose; preserve exact quotes and proper nouns; output only the summary itself.
If a [Previous summary] is provided: carry forward still-relevant requirements and content from it; where old and new conflict, the newer wins."),
        ("角色扮演",
            @"You are compacting a roleplay conversation for an AI character assistant. Summarize it (including any [Previous summary]) into a structured memory summary. Write the content in the same language as the conversation; keep the section headings exactly in English. Output exactly these sections, keeping every one (write ""(none)"" when empty):

## Characters & Relationships
- who is involved, how they relate, tone of each relationship (keep exact names verbatim)
## Plot & Events
- story beats in chronological order with emotional significance; merge duplicates but never drop facts
## World & Continuity
- established settings, locations, items and rules that must stay consistent
## Commitments & Promises
- promises made in character (unfulfilled ones MUST be kept); sensitive topics
## Unresolved Threads
- open plot lines, pending scenes, what the user expects next

Rules: terse bullets, not prose; preserve exact names, place names and quoted lines; output only the summary itself, do not mention that a summary was made.
If a [Previous summary] is provided: carry forward unfulfilled commitments and established continuity from it; where old and new conflict, the newer wins."),
    };

    /// <summary>设置页组合框"自定义"项的标记值(内置预设的 Tag 即其名称)。</summary>
    public const string CustomPresetTag = "__custom__";

    /// <summary>解析实际生效的摘要模板:选定内置预设 → 该预设正文;自定义(或旧版无预设数据)→ 用户模板,留空回退缺省。</summary>
    public static string ResolveTemplate(string? preset, string? customText)
    {
        var name = preset ?? "";
        if (name.Length > 0 && !string.Equals(name, CustomPresetTag, StringComparison.Ordinal))
            return Presets.FirstOrDefault(p => p.Name == name).Text is { Length: > 0 } t ? t : DefaultTemplate;
        return string.IsNullOrWhiteSpace(customText) ? DefaultTemplate : customText!;
    }

    /// <summary>摘要消息的固定前缀:上下文中 system 之后的首条用户消息以此开头即为此前压缩生成的摘要(重启后据此续接)。</summary>
    private const string SummaryPrefix = "[Conversation summary of earlier messages]\n";

    /// <summary>
    /// 由宿主提供:该会话生效的上下文 Token 上限(会话级模型各自的设定);0 = 无上限,不压缩。
    /// </summary>
    public Func<AgentSession, int>? ContextLimit { get; set; }
    public bool AutoCompress { get; set; } = true;

    /// <summary>压缩触发阈值:占 Token 上限的百分比(10-95)。</summary>
    public int CompressThreshold { get; set; } = 80;

    /// <summary>保留窗口:最近 x% tokens 的整条消息(占最大上下文,1-90);从最新消息往前累计。</summary>
    public int KeepPercent { get; set; } = 20;

    /// <summary>摘要长度上限:最大上下文的 y%(1-50);经 max_tokens 传给 LLM。截断策略下不使用(值保持原样,切回 summarize 仍有效)。</summary>
    public int SummaryMaxPercent { get; set; } = 10;

    /// <summary>压缩策略:summarize(AI 总结旧消息)/ truncate(保留窗口之外直接丢弃,不调用 LLM)。</summary>
    public string Strategy { get; set; } = "summarize";

    /// <summary>总结器的 system prompt 模板(设置页可改);token 预算说明自动前置在模板之前。</summary>
    public string Template { get; set; } = DefaultTemplate;

    /// <summary>压缩开始/成功完成(消息链钩子运行在 UI 线程,宿主可据此显示"正在压缩上下文"状态行;失败或取消不发完成)。</summary>
    public event Action<AgentSession>? CompressionStarted;
    public event Action<AgentSession>? CompressionFinished;
    /// <summary>压缩失败(已发开始事件但总结未产出摘要;取消/截断不触发):宿主把「压缩中」行收口为「压缩失败」。</summary>
    public event Action<AgentSession>? CompressionFailed;

    public override string Name => "context-compression";
    public override void Initialize() { }
    public override void Dispose() { }

    // ───────── 开跑检查的挂起标记 ─────────
    // OnBeforeRunAsync 接口不传取消令牌,开跑压缩直接挂在那里只能传 CancellationToken.None——
    // 用户点停止后,大上下文的压缩总结请求仍会把几十秒的调用跑完才停(停止按钮经 session.CancelRun
    // 取消的是运行令牌,压缩不接它就无从中断)。框架在每次 LLM 请求发出前经 OnBeforeRequestAsync
    // 传入会话运行令牌,故开跑检查改为:OnBeforeRunAsync 只挂标记,首个请求前(带令牌处)执行。
    // 标记每次运行只消费一次:轮中的阈值检查仍由 OnAfterTurnAsync 负责——若每个请求前都查,
    // OnAfterTurnAsync 刚压缩完后的下一请求会读到尚未刷新的占用而重复压缩(把刚生成的摘要再总结一遍)。
    // ConditionalWeakTable:按会话挂标记,线程安全且不阻止会话被回收。
    private readonly ConditionalWeakTable<AgentSession, object> _pendingStartCheck = new();
    private static readonly object StartCheckMarker = new();

    public override Task OnBeforeRunAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages)
    {
        _pendingStartCheck.Remove(session);
        _pendingStartCheck.Add(session, StartCheckMarker); // 挂起开跑检查,首个请求前执行(见 OnBeforeRequestAsync)
        return Task.CompletedTask;
    }

    // 注意:参数必须与接口一致地声明为可选(= default)——接口成员带可选参数时,
    // 实现方缺省声明不匹配,CLR 不会绑定本方法,调用会落到接口的空默认实现上(静默失效)
    public override async Task OnBeforeRequestAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (!_pendingStartCheck.Remove(session))
            return; // 非本次运行的首个请求:轮中检查由 OnAfterTurnAsync 负责
        await CheckAndCompressAsync(agent, session, messages, cancellationToken);
    }

    /// <summary>每次请求(轮次)结束后检查:一轮内多次工具调用会持续推高上下文,请求一结束就重算阈值,
    /// 达到立即压缩,确保下一次请求不超限;单轮运行在结果写回后的这一次检查是瘦身本轮产出的唯一机会
    /// (运行开始时的检查只能瘦身历史,当时本轮产出还不存在)。</summary>
    // 注意:参数必须与接口一致地声明为可选(= default)——接口成员带可选参数时,
    // 实现方缺省声明不匹配,CLR 不会绑定本方法,调用会落到接口的空默认实现上(静默失效)
    public override async Task OnAfterTurnAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        await CheckAndCompressAsync(agent, session, messages, cancellationToken);
    }

    /// <summary>压缩触发检查:占用达到阈值(优先取供应商回报的真实 prompt tokens,本地估算通常偏低)则压缩。
    /// 运行中压缩会重写工作消息并写回会话历史,Agent 侧的写回基线在结束时重新定位,二者配合安全。</summary>
    private async Task CheckAndCompressAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken token)
    {
        if (!AutoCompress)
        {
            Log.Global.Debug("[compression] skip: AutoCompress off");
            return;
        }
        var limit = ContextLimit?.Invoke(session) ?? 0;
        if (limit <= 0 || messages.Count == 0)
        {
            Log.Global.Debug($"[compression] skip: limit={limit}, messages={messages.Count}");
            return;
        }

        // 触发判断只认服务端回报的 KV 用量(每次 LLM 请求都会回报,最准确);本地估算不可靠,不参与判断
        var total = session.LastPromptTokens;
        if (total <= 0)
        {
            Log.Global.Debug("[compression] skip: no KV usage reported yet");
            return;
        }
        var threshold = (long)limit * Math.Clamp(CompressThreshold, 10, 95) / 100;
        if (total < threshold)
            return; // 未到阈值:逐请求都发生,不记日志(记了曾占日志 2%)
        Log.Global.Info($"[compression] triggered: total={total} >= threshold={threshold} (limit={limit})");

        await CompressCoreAsync(agent, session, messages, null, token: token);
    }

    /// <summary>手动压缩的结果:宿主据此提示(NothingToCompress → toast;Failed → 「压缩失败」分割线;Ok → 「会话已压缩」)。</summary>
    public enum CompressOutcome
    {
        Ok, // 压缩完成
        Failed, // 总结未产出摘要(已发 CompressionFailed,宿主落「压缩失败」行)
        NothingToCompress, // 无上限/无消息/保留窗口已覆盖全部:压无可压
    }

    /// <summary>手动触发压缩(聊天 /compact):跳过阈值检查立即压缩,extraInstruction 追加到摘要模板尾部(summarize 生效)。
    /// 占用会话请求锁:与正常运行互斥,停止按钮(CancelRun)可中断。</summary>
    public async Task<CompressOutcome> CompressNowAsync(AgentClass agent, AgentSession session, string? extraInstruction)
    {
        if ((ContextLimit?.Invoke(session) ?? 0) <= 0 || session.Messages.Count == 0)
            return CompressOutcome.NothingToCompress; // 无上限:保留窗口算不出来

        var systemPrompt = agent.BuildSystemPrompt(session);
        if (string.IsNullOrEmpty(systemPrompt))
            return CompressOutcome.NothingToCompress; // 无 system 的退化场景(与自动压缩不写回一致)

        var messages = new List<ChatMessage>(session.Messages);
        var systemMessage = new ChatMessage(ChatRole.System, systemPrompt);
        systemMessage.TokenCount = TokenEstimator.EstimateMessage(systemMessage);
        messages.Insert(0, systemMessage);

        var token = session.BeginRun(CancellationToken.None); // 请求锁:同会话运行中会抛异常(UI 侧已先拦截)
        try
        {
            return await CompressCoreAsync(agent, session, messages, extraInstruction, force: true, token);
        }
        finally
        {
            session.EndRun();
        }
    }

    /// <summary>压缩核心:计算保留窗口 → 按策略执行(summarize/truncate)→ 写回会话历史。
    /// force = 手动触发:上下文已在保留窗口内时收缩到只留最新一条消息,一律压缩。</summary>
    private async Task<CompressOutcome> CompressCoreAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, string? extraInstruction, bool force = false, CancellationToken token = default)
    {
        var limit = ContextLimit?.Invoke(session) ?? 0;

        // 保留窗口:从最新消息往前累计整条消息的 token,直到达到上限的 KeepPercent%(system 不参与累计)。
        // 真实占用以服务端回报的 KV 用量为准;本地估算不含工具定义等请求级开销,两者差距大时
        // 按真实/估算比例把窗口预算缩放到估算尺度,使新旧消息的划分随真实占用走
        var localSum = messages.Skip(1).Sum(TokenEstimator.TokenCount);
        var trueTotal = Math.Max(session.LastPromptTokens, localSum);
        var keepTokens = (long)limit * Math.Clamp(KeepPercent, 1, 90) / 100;
        var keepBudget = trueTotal > localSum && localSum > 0 ? keepTokens * localSum / trueTotal : keepTokens;
        var start = messages.Count;
        long accumulated = 0;
        for (var i = messages.Count - 1; i >= 1; i--)
        {
            if (accumulated > 0 && accumulated + TokenEstimator.TokenCount(messages[i]) > keepBudget)
                break; // 已达标且再加一条整条消息会超 → 停(不拆单条消息)
            accumulated += TokenEstimator.TokenCount(messages[i]);
            start = i;
        }

        // 窗口起点不得是孤儿 Tool 消息(向前吞掉配对的助手消息)
        while (start > 1 && messages[start].Role == ChatRole.Tool)
            start--;

        var oldMessages = messages.Skip(1).Take(start - 1).ToList();
        if (oldMessages.Count == 0 && force)
        {
            // 手动压缩:上下文整体都在保留窗口内 → 收缩到只留最新一条消息(Tool 孤儿保护同上),一律压缩
            start = messages.Count - 1;
            while (start > 1 && messages[start].Role == ChatRole.Tool)
                start--;
            oldMessages = messages.Skip(1).Take(start - 1).ToList();
        }
        if (oldMessages.Count == 0)
        {
            // 触发了阈值但保留窗口已覆盖全部消息:占用主要来自 system+工具定义等压缩不触碰的部分,
            // 或会话过短——压无可压,留日志说明(否则"triggered"后一片静默,看似没触发)
            Log.Global.Debug($"[compression] nothing to compress: trueTotal={trueTotal} localSum={localSum}, keep window covers all messages");
            return CompressOutcome.NothingToCompress;
        }

        // 写回会话历史的前提是 working[0] 为纯 system(此时 session.Messages = working[1..末-1]);
        // 无 system 时索引错位,退化为仅影响工作消息(旧行为),不写回以免损坏历史
        var persist = messages[0].Role == ChatRole.System;

        // 截断策略:保留窗口之外直接丢弃(不调用 LLM、不生成摘要);已有的旧摘要消息保留,避免丢掉已压缩的信息
        if (string.Equals(Strategy, "truncate", StringComparison.OrdinalIgnoreCase))
        {
            var keepLeading = HasLeadingSummary(messages) ? 1 : 0;
            var truncated = new List<ChatMessage>(messages.Count) { messages[0] }; // system
            if (keepLeading > 0)
                truncated.Add(messages[1]);
            truncated.AddRange(messages.Skip(start));
            messages.Clear();
            messages.AddRange(truncated);
            // 写回会话历史:被截断的旧消息从上下文中真正删除,下次载入与关闭前一致
            if (persist)
                session.ReplacePrefix(start - 1 - keepLeading, null);
            CompressionFinished?.Invoke(session); // 截断即时完成(无齿轮行),同样留下分割线标记
            return CompressOutcome.Ok;
        }

        var summaryMaxTokens = Math.Max(64, (int)((long)limit * Math.Clamp(SummaryMaxPercent, 1, 50) / 100));
        // 上一轮摘要取自上下文的头部摘要消息(已随会话落盘,重启后仍可续接),不再依赖内存状态
        CompressionStarted?.Invoke(session);
        // system prompt(persist 时 messages[0] 即它):角色设定/身份只在这里,传给总结器才能正确引用名字
        var (summary, usage) = await SummarizeAsync(agent, session, messages, start, ExtractLeadingSummary(messages), oldMessages,
            HasLeadingSummary(messages), persist ? messages[0].Content ?? "" : "", summaryMaxTokens, extraInstruction, token);
        LastCompressionUsage = usage; // 空闲手动压缩时由宿主消费补记
        if (usage is not null)
            await agent.ReportUsageAsync(session, usage, token); // RunAsync 内:与正常轮次同链;空闲:空操作
        if (string.IsNullOrWhiteSpace(summary))
        {
            CompressionFailed?.Invoke(session); // 总结未产出:通知宿主收口为「压缩失败」
            return CompressOutcome.Failed;
        }
        CompressionFinished?.Invoke(session);

        summary = summary.Trim();
        var summaryMessage = new ChatMessage(ChatRole.User, SummaryPrefix + summary);
        // 摘要置于消息头部:紧随 system、早于保留的近期消息
        var compressed = new List<ChatMessage>(messages.Count)
        {
            messages[0], // system
            summaryMessage,
        };
        compressed.AddRange(messages.Skip(start));
        messages.Clear();
        messages.AddRange(compressed);
        // 写回会话历史:旧消息换成摘要消息,重启后载入的上下文即压缩态(Display 不受影响)
        if (persist)
            session.ReplacePrefix(start - 1, summaryMessage);
        return CompressOutcome.Ok;
    }

    /// <summary>总结旧消息:优先走前缀缓存路径(真实上下文保留到保留窗口边界、压缩模板追加为末尾的用户消息,
    /// 旧消息是上一轮请求的严格前缀,整段命中 KV 缓存,只有模板按全价计费);该路径失败(请求异常/摘要为空/
    /// 模型改回工具调用)时回退到合成转录稿的独立请求(模板进 system,旧行为),保证任何情况下都能产出摘要。</summary>
    private async Task<(string Summary, APet.Framework.LLM.LlmUsage? Usage)> SummarizeAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, int start,
        string previousSummary, List<ChatMessage> oldMessages, bool hasLeadingSummary, string systemPrompt, int maxTokens,
        string? extraInstruction, CancellationToken token)
    {
        APet.Framework.LLM.LlmUsage? usage = null;
        try
        {
            var (summary, u) = await SummarizeByPrefixAsync(agent, session, messages, start, hasLeadingSummary, maxTokens, extraInstruction, token);
            usage = u;
            if (!string.IsNullOrWhiteSpace(summary))
                return (summary, usage);
            Log.Global.Info("Prefix-cache compaction returned an empty summary; falling back to transcript mode");
        }
        catch (OperationCanceledException)
        {
            throw; // 用户取消:直接中断,不回退
        }
        catch (Exception ex)
        {
            Log.Global.Error("Prefix-cache compaction failed; falling back to transcript mode", ex);
        }
        var (fb, fbUsage) = await SummarizeByTranscriptAsync(agent, session, previousSummary, oldMessages, hasLeadingSummary,
            systemPrompt, maxTokens, extraInstruction, token);
        return (fb, SumUsage(usage, fbUsage)); // 回退模式下两段请求的用量都要计入
    }

    /// <summary>两次请求的用量相加(前缀路径请求成功但摘要为空 → 回退时,两段都是真实请求)。</summary>
    private static APet.Framework.LLM.LlmUsage? SumUsage(APet.Framework.LLM.LlmUsage? a, APet.Framework.LLM.LlmUsage? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new(a.PromptTokens + b.PromptTokens, a.CompletionTokens + b.CompletionTokens,
            a.TotalTokens + b.TotalTokens, a.CachedTokens + b.CachedTokens, 0, a.ReasoningTokens + b.ReasoningTokens);
    }

    /// <summary>前缀缓存路径:请求消息 = [system + 旧消息(同一批 ChatMessage 对象、原顺序,截到保留窗口边界)]
    /// + 压缩模板(用户消息)。工具定义必须与正常轮次一致:OpenAI 兼容接口的缓存前缀从 tools 开始,
    /// 缺了 tools 整段缓存照样 miss。返回空串(如模型回了工具调用而非摘要)由调用方回退。</summary>
    private async Task<(string Content, APet.Framework.LLM.LlmUsage? Usage)> SummarizeByPrefixAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages,
        int start, bool hasLeadingSummary, int maxTokens, string? extraInstruction, CancellationToken token)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Please compress the conversation above into a summary of at most {maxTokens} tokens.");
        if (hasLeadingSummary)
            sb.AppendLine("The conversation begins with the previous summary (the message starting with \"[Conversation summary of earlier messages]\"); treat it as the [Previous summary] referred to below.");
        sb.AppendLine(Template);
        if (!string.IsNullOrWhiteSpace(extraInstruction))
            sb.AppendLine(extraInstruction.Trim()); // /compact 附加说明:接到模板尾部

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>(messages.Take(start)) { new(ChatRole.User, sb.ToString()) },
            Tools = agent.Tools.Count > 0 ? agent.Tools.GetDefinitions() : null,
            MaxTokens = maxTokens,
        };
        var response = await session.LlmClient.CompleteAsync(request, token);
        // usage 交调用方统一上报(RunAsync 内进回调链;空闲手动压缩由宿主消费 LastCompressionUsage)
        return (response.Message.Content ?? "", response.Usage);
    }

    /// <summary>回退路径(旧行为):调用会话的 LLM 非流式总结合成转录稿(带上一轮摘要,避免信息丢失;max_tokens 约束长度)。
    /// 与正常轮次前缀零重合、全价计费,但不依赖会话上下文的形态。
    /// systemPrompt 单独传入:角色设定/身份只存在于 system prompt、不在对话消息里,不传则摘要无法正确引用名字与关系。
    /// extraInstruction(/compact 的附加说明)追加在模板尾部。</summary>
    private async Task<(string Content, APet.Framework.LLM.LlmUsage? Usage)> SummarizeByTranscriptAsync(AgentClass agent, AgentSession session, string previousSummary, List<ChatMessage> oldMessages, bool hasLeadingSummary, string systemPrompt, int maxTokens, string? extraInstruction, CancellationToken token)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            var sp = systemPrompt.Trim();
            if (sp.Length > MaxSystemPromptChars) sp = sp[..MaxSystemPromptChars] + "…"; // 防御异常长设定,正常角色设定远低于此
            sb.AppendLine("[System prompt]").AppendLine(sp).AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(previousSummary))
            sb.AppendLine("[Previous summary]").AppendLine(previousSummary.Trim()).AppendLine();
        sb.AppendLine("[Conversation to summarize]");
        // 头部旧摘要已经 [Previous summary] 传入,转录中跳过,避免重复计数
        var body = hasLeadingSummary ? oldMessages.Skip(1) : oldMessages;
        foreach (var message in body)
        {
            var content = message.Content ?? "";
            if (content.Length > 800)
                content = content[..800] + "…";
            sb.AppendLine($"{message.Role}: {content}");
            if (sb.Length > MaxTranscriptChars)
            {
                sb.AppendLine("(older messages truncated)");
                break;
            }
        }

        var systemText = $"Please compress the following conversation into a summary of at most {maxTokens} tokens.\n\n" + Template;
        if (!string.IsNullOrWhiteSpace(extraInstruction))
            systemText += "\n\n" + extraInstruction.Trim(); // /compact 附加说明:接到模板尾部
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemText),
                new(ChatRole.User, sb.ToString()),
            },
            MaxTokens = maxTokens,
        };
        var response = await session.LlmClient.CompleteAsync(request, token);
        // usage 交调用方统一上报(RunAsync 内进回调链;空闲手动压缩由宿主消费 LastCompressionUsage)
        return (response.Message.Content ?? "", response.Usage);
    }

    /// <summary>会话上下文中当前已持久化的摘要(首条为带摘要前缀的用户消息);无则 null(/summary 用)。</summary>
    public static string? GetSummaryText(AgentSession session)
    {
        var first = session.Messages.Count > 0 ? session.Messages[0] : null;
        if (first?.Role != ChatRole.User) return null;
        var content = first.Content ?? "";
        return content.Length > SummaryPrefix.Length && content.StartsWith(SummaryPrefix, StringComparison.Ordinal)
            ? content.Substring(SummaryPrefix.Length).Trim()
            : null;
    }

    /// <summary>system 之后第一条是否为之前压缩生成的旧摘要(截断模式下保留它;总结模式下其正文作为上一轮摘要续接)。</summary>
    private static bool HasLeadingSummary(List<ChatMessage> messages) =>
        messages.Count > 1
        && messages[1].Role == ChatRole.User
        && (messages[1].Content ?? "").StartsWith(SummaryPrefix, StringComparison.Ordinal);

    /// <summary>提取头部摘要消息的正文(无则返回空串)。摘要已随会话上下文落盘,重启后从这里恢复上一轮摘要。</summary>
    private static string ExtractLeadingSummary(List<ChatMessage> messages)
    {
        if (!HasLeadingSummary(messages)) return "";
        var content = messages[1].Content ?? "";
        return content.Length > SummaryPrefix.Length ? content.Substring(SummaryPrefix.Length).Trim() : "";
    }
}
