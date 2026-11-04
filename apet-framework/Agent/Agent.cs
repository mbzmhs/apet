using System.Text;
using APet.Framework.Events;
using APet.Framework.LLM;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Session;
using APet.Framework.Tools;

namespace APet.Framework.Agent;

/// <summary>
/// Agent 核心:编排 LLM 通讯与工具调用,直到模型给出最终回复。
/// 自身只保存 LLM 配置与工具/插件配置,会话上下文由传入的 AgentSession 持有。
/// </summary>
public sealed class Agent
{
    private readonly AgentOptions _options;
    private readonly ToolRegistry _tools;
    private readonly ILog _log;

    // 环外用量上报通道按运行实例隔离:一个 Agent 服务多会话(用户会话 + B站直播触发轮并发),
    // 单字段会互踩——后启动的覆盖前者、先结束的 finally 置 null 掐断后者的通道,
    // 压缩等环外用量被记错会话甚至丢失。AsyncLocal 随各轮异步控制流自然隔离。
    private static readonly AsyncLocal<IAgentCallbacks?> s_activeCallbacks = new();

    /// <summary>环外代表会话发出的 LLM 请求(如上下文压缩总结)上报用量:进入与主循环同一条 OnUsageReported
    /// 回调链(前端统计、计费累计);未在运行时为空操作。</summary>
    public Task ReportUsageAsync(AgentSession session, LlmUsage usage, CancellationToken cancellationToken = default)
        => s_activeCallbacks.Value?.OnUsageReportedAsync(session, usage, cancellationToken) ?? Task.CompletedTask;

    public Agent(AgentOptions options, ToolRegistry? toolRegistry = null, PluginRegistry? pluginRegistry = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tools = toolRegistry ?? new ToolRegistry();
        Plugins = pluginRegistry ?? new PluginRegistry();
        _log = options.Log ?? Log.Global;
        // 启用了 IsTool 的插件经适配器进入 ToolRegistry 的动态工具管线(定义随请求发送,执行走统一鉴权);
        // 提供器枚举全部插件而非仅启用者:注册中心按 Enabled 过滤(禁用插件的工具不出现在 LLM 定义与执行),
        // 设置界面经 GetAllTools 可见插件关闭状态,便于从工具页重新开启
        _tools.SetDynamicToolsProvider(() => Plugins.All
            .Select(p => p.Plugin)
            .OfType<AgentPluginBase>()
            .Where(p => p.IsTool)
            .Select(p => (IApetTool)new PluginToolAdapter(p)));
    }

    public ToolRegistry Tools => _tools;

    /// <summary>插件注册中心:插件可贡献 system prompt、介入消息链、控制运行中断。</summary>
    public PluginRegistry Plugins { get; }

    /// <summary>根据 Agent 保存的 LLM 配置创建新会话(会话持有独立的连接实例)。LLM 日志留空时继承 Agent 日志。</summary>
    public AgentSession CreateSession()
    {
        var source = _options.Llm;
        var llmOptions = new LlmClientOptions
        {
            BaseUrl = source.BaseUrl,
            ApiKey = source.ApiKey,
            Model = source.Model,
                ReasoningEffort = source.ReasoningEffort, // 会话连接必须继承思考等级:否则新会话回落到默认 off→服务端默认开启思考(重启后 none 仍思考的根因)
                SendThinkingField = source.SendThinkingField, // 旧开关;思考字段样式随供应商:Gemini 等端点不支持该字段
                ThinkingStyle = source.ThinkingStyle,
            Log = source.Log ?? _options.Log,
        };
        var session = new AgentSession(new OpenAiCompatibleClient(llmOptions));
        FireSessionEvent(session, SessionEventKind.Created); // 新对话/分叉/磁盘还原/移出归档共用入口
        return session;
    }

    /// <summary>
    /// 会话移除通知(删除/归档):宿主在会话离开运行时前调用,插件据此清理按会话维护的状态。
    /// 插件抛出的异常不会回传(仅记日志)。
    /// </summary>
    public void NotifySessionRemoved(AgentSession session) =>
        FireSessionEvent(session, SessionEventKind.Removed);

    /// <summary>会话生命周期事件分发:逐个启用插件调用,单个插件失败只记日志,不影响其余插件与会话本身。</summary>
    private void FireSessionEvent(AgentSession session, SessionEventKind kind)
    {
        foreach (var registration in Plugins.EnabledPlugins)
        {
            try
            {
                registration.Plugin.OnSessionEvent(this, session, kind);
            }
            catch (Exception ex)
            {
                _log.Error($"Plugin {registration.Name} OnSessionEvent({kind}) failed: {ex}");
            }
        }
    }

    /// <summary>运行结束钩子分发(按注册顺序串行 await):插件失败只记日志——运行时已终结,异常无处回传。</summary>
    private async Task FireRunFinishedAsync(AgentSession session, RunOutcome outcome)
    {
        foreach (var registration in Plugins.EnabledPlugins)
        {
            try
            {
                await registration.Plugin.OnRunFinishedAsync(this, session, outcome).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Plugin {registration.Name} OnRunFinishedAsync({outcome.Kind}) failed: {ex}");
            }
        }
    }

    /// <summary>
    /// 运行一轮对话:流式转发回调 → 需要时循环执行工具 → 完成后把新消息写入 Session 并触发 OnCompleted。
    /// securityPolicy 为空时使用 Agent 配置的默认策略;可传入会话级策略(如各会话自己的工作目录)。
    /// imageAttachments:用户随输入附带的图片(data URL),以多模态内容并入本轮用户消息(供视觉模型查看)。
    /// extraTextParts:用户附加的文本片段(如文本文件附件全文),每段在 content 数组中独立成 text 片段,
    /// 与 userInput 结构隔离——弱模型更易区分"用户原话"与"文件内容"的边界。
    /// clientId:宿主分配的用户消息标识(盖到本轮用户消息上,写回 Session 后供宿主做 display↔context 匹配;不发给模型)。
    /// preQueuedMessages:宿主开跑前显式传入的队列存量(自动派发场景),与会话内新到插话一起按 FIFO 拼在输入之前。
    /// 中途消息队列在每次请求发出前收编,两个消费点:开跑组装时(早于本轮输入到达的插话)插在输入之前;
    /// 开跑后(首轮与工具轮)按 FIFO 追加在本轮待发内容之尾——输入之后/上一轮"助手+工具结果"块之后,
    /// 与真实时序一致(插话发生在这些内容之后),且不拆散 tool_calls 与其结果的配对。
    /// 出错时触发 OnError 后将异常重新抛出。
    /// </summary>
    public async Task<AgentResult> RunAsync(
        AgentSession session,
        string userInput,
        IAgentCallbacks callbacks,
        Security.AgentSecurityPolicy? securityPolicy = null,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? imageAttachments = null,
        IEnumerable<string>? extraTextParts = null,
        string? clientId = null,
        IEnumerable<QueuedUserMessage>? preQueuedMessages = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(callbacks);

        var images = imageAttachments?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        var extraTexts = extraTextParts?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (string.IsNullOrEmpty(userInput) && images is not { Count: > 0 } && extraTexts is not { Count: > 0 })
            throw new ArgumentException("userInput, imageAttachments and extraTextParts must not all be empty.", nameof(userInput));

        var runToken = session.BeginRun(cancellationToken); // 请求锁:同会话未结束前再次请求直接拒绝
        List<ChatMessage> working;
        ChatMessage inputMessage;
        ChatMessage? firstQueued;
        try
        {
            working = BuildWorkingMessages(session, userInput, images, extraTexts,
                preQueuedMessages?.ToList(), out inputMessage, out firstQueued);
            if (clientId is not null)
                inputMessage.ClientId = clientId; // 宿主标识随本轮写回 Session:/undo 据此与 display 气泡精确匹配
            inputMessage.Id = clientId ?? Guid.NewGuid().ToString("N"); // 消息 id 随会话文件落盘:用户消息复用宿主标识(与 display 气泡同一 Id)
            inputMessage.TimeCreated = DateTime.Now;
        }
        catch
        {
            // 组装期失败(含队列排空等宿主事件回调抛出):必须释放请求锁再上抛——
            // 此处位于主 try 之外,main finally 的 EndRun 不会执行,漏放则 IsRunning 永久为真,
            // 会话此后每次请求都被 "already has a request in flight" 拒绝,只能重启
            session.EndRun();
            throw;
        }

        // 增量写回状态(声明在 try 外,中断/异常路径也能补写):每轮迭代完成即把"助手与工具结果成对"的消息
        // 写回会话,长任务被打断或异常时已完成部分不丢;未完成迭代不成对,不写,避免孤儿 tool_calls 污染上下文。
        var writeIndex = 0; // 工作消息中已写回会话的边界
        var completedIndex = 0; // 已完成迭代的工作消息边界
        var flushed = new List<ChatMessage>();
        var outcome = RunOutcome.Failed("run did not start"); // 三态结局,finally 交给 OnRunFinishedAsync

        void Flush()
        {
            if (working.Count == 0)
                return;
            // 写回基线重新定位:运行中压缩会重排工作消息。以最后一条已写回消息为哨兵找新位置;
            // 找不到说明它被摘要替换(压缩把已写回前缀整体换成了 system+summary),此后均未写回。
            if (writeIndex > 0 && writeIndex <= working.Count)
            {
                var sentinel = working[writeIndex - 1];
                var idx = working.IndexOf(sentinel);
                writeIndex = idx >= 0 ? idx + 1 : Math.Min(2, working.Count);
            }
            if (completedIndex > writeIndex && completedIndex <= working.Count)
            {
                var batch = working.GetRange(writeIndex, completedIndex - writeIndex);
                session.AddMessages(batch);
                flushed.AddRange(batch);
                writeIndex = completedIndex;
            }
        }

        try
        {
            s_activeCallbacks.Value = callbacks; // 环外用量上报通道(压缩总结等,见 ReportUsageAsync),随本轮异步流隔离,finally 清空
            foreach (var registration in Plugins.EnabledPlugins)
            {
                await registration.Plugin.OnBeforeRunAsync(this, session, working);
            }
            // 写回基线:运行前压缩可能已重排工作消息,以第一条新增消息的位置为准(队列插话在输入之前,一并写回)
            writeIndex = working.IndexOf(firstQueued ?? inputMessage);
            if (writeIndex < 0)
                writeIndex = working.IndexOf(inputMessage);
            if (writeIndex < 0)
                writeIndex = working.Count; // 输入被插件移除:不写回,避免把保留窗口重复写进历史
            completedIndex = writeIndex;

            // 用户输入立刻落盘(含此前收编的队列插话):生成中按停止,用户消息不再随中断一并消失,
            // /undo 也始终有回滚锚点。助手/工具仍按「完整迭代成对写回」节奏(未完成不成对,不写,
            // 避免孤儿 tool_calls 污染上下文);落库时机在宿主的运行收尾 SaveSession(中断路径同样执行)
            var inputIdx = working.IndexOf(inputMessage);
            if (inputIdx >= writeIndex)
            {
                var head = working.GetRange(writeIndex, inputIdx - writeIndex + 1);
                session.AddMessages(head);
                flushed.AddRange(head);
                writeIndex = inputIdx + 1;
                completedIndex = Math.Max(completedIndex, writeIndex);
            }

            _log.Info($"Agent run started session={session.Id} context={session.Messages.Count} messages, input={(userInput?.Length ?? 0)} chars, images={images?.Count ?? 0}, textParts={extraTexts?.Count ?? 0}");

            for (int iteration = 1; ; iteration++)
            {
                if (_options.MaxToolIterations > 0 && iteration > _options.MaxToolIterations)
                    throw new LlmException($"Max tool-call iterations ({_options.MaxToolIterations}) reached; the task may not converge.");

                // 中途消息队列:每次请求发出前收编插话,按 FIFO 追加在本轮待发内容之尾——首轮在输入之后、
                // 工具轮在上一轮"助手+工具结果"块之后(插话发生在这些内容之后,时序真实,也不拆散配对)。
                // completedIndex 同步前移:用户消息是原子安全的,请求中途被中断时随最近一次 Flush 一并写回会话。
                // stopAtDeferred:命令样与"本轮后发送"条目绝不拼进进行中请求,留在队列等空闲态派发处理
                var drained = session.DrainQueued(stopAtDeferred: true);
                if (drained.Count > 0)
                {
                    var queuedMessages = drained.Select(BuildQueuedChatMessage).ToList();
                    working.AddRange(queuedMessages);
                    completedIndex = working.Count;
                    session.RaiseQueueDrained(drained, atRunStart: false);
                    _log.Info($"Agent queue drained mid-run: {drained.Count} message(s) appended before request");
                }

                // 每轮请求前的异步检查(如上下文压缩):工具轮中途也能触发压缩,防止长任务撑爆上下文
                foreach (var registration in Plugins.EnabledPlugins)
                {
                    await registration.Plugin.OnBeforeRequestAsync(this, session, working, runToken);
                }

                // 插件消息链:请求前,可查看/增删/修改消息,抛异常强行中断
                foreach (var registration in Plugins.EnabledPlugins)
                {
                    registration.Plugin.OnBeforeRequest(this, session, working);
                }

                var (assistant, finishReason, usage) = await StreamTurnAsync(session, working, callbacks, runToken);
                if (usage is not null)
                    session.LastPromptTokens = usage.PromptTokens; // 供应商回报的真实占用,供压缩触发与界面显示

                // 插件消息链:助手消息组装完成,可修改内容,抛异常强行中断(该轮不写入 Session)
                foreach (var registration in Plugins.EnabledPlugins)
                {
                    registration.Plugin.OnAssistantMessage(this, session, assistant);
                }

                working.Add(assistant);
                _log.Info($"Model turn {iteration} finished finish_reason={finishReason} content={assistant.Content?.Length ?? 0} chars, toolCalls={assistant.ToolCalls?.Count ?? 0}");
                await callbacks.OnAssistantMessageAsync(session, assistant, runToken);

                if (finishReason != FinishReason.ToolCalls || assistant.ToolCalls is not { Count: > 0 })
                {
                    completedIndex = working.Count; // 最后一轮:助手即最终回复
                    Flush();
                    var result = new AgentResult
                    {
                        Content = assistant.Content,
                        FinalMessage = assistant,
                        NewMessages = flushed,
                        Usage = usage,
                    };
                    await callbacks.OnCompletedAsync(session, result, runToken);
                    _log.Info($"Agent run completed session={session.Id} turns={iteration}, {flushed.Count} new messages written to session");
                    // 末轮请求结束后的检查:单轮运行只有这一次机会瘦身本轮产出(运行开始时的检查只瘦身历史)
                    foreach (var registration in Plugins.EnabledPlugins)
                    {
                        await registration.Plugin.OnAfterTurnAsync(this, session, working, runToken);
                    }
                    outcome = RunOutcome.Completed;
                    return result;
                }

                foreach (var call in assistant.ToolCalls)
                {
                    _log.Info($"Executing tool {call.Name} call_id={call.Id}");
                    await callbacks.OnToolCallStartedAsync(session, call, runToken);

                    // 工具实时输出出口:工具(ToolExecutionScope.Output)推送的增量转给宿主回调;
                    // 转发失败(宿主展示层故障)不得影响工具本身,异常只记日志
                    void RelayOutput(string stream, string delta)
                    {
                        try
                        {
                            var relay = callbacks.OnToolCallOutputAsync(session, call, stream, delta, runToken);
                            if (!relay.IsCompletedSuccessfully)
                                _ = relay.ContinueWith(t => _log.Debug(
                                    $"Tool {call.Name} output relay failed: {t.Exception?.GetBaseException().Message}"),
                                    TaskContinuationOptions.OnlyOnFaulted);
                        }
                        catch (Exception ex)
                        {
                            _log.Debug($"Tool {call.Name} output relay failed: {ex.Message}");
                        }
                    }

                    var toolResult = await _tools.ExecuteAsync(
                        call.Name,
                        call.ArgumentsJson,
                        securityPolicy ?? _options.Security,
                        // 鉴权结论先交给宿主确认,宿主可采纳或改判(放行/拒绝)
                        (effect, decision) => callbacks.OnToolCallAuthorizationAsync(session, call, effect, decision, runToken),
                        runToken,
                        // 实时工作消息 Token 用量:供按"剩余上下文"计算返回预算的工具(如记忆插件)
                        () => working.Sum(TokenEstimator.TokenCount),
                        RelayOutput);
                    await callbacks.OnToolCallFinishedAsync(session, call, toolResult, runToken);
                    _log.Info($"Tool {call.Name} finished, result ({toolResult.Text.Length} chars, {toolResult.ImageDataUrls.Count} image(s)) returned to LLM");
                    var toolMessage = new ChatMessage(ChatRole.Tool, toolResult.Text)
                    {
                        ToolCallId = call.Id,
                        Id = Guid.NewGuid().ToString("N"), // 工具结果消息同样带 id/时刻:落盘后全量消息可寻址
                        TimeCreated = DateTime.Now,
                    };
                    toolMessage.TimeCompleted = toolMessage.TimeCreated;
                    if (toolResult.ImageDataUrls.Count > 0)
                    {
                        // 多模态工具结果:文本 + 图片片段以 content 数组回传(供视觉模型直接查看)
                        var parts = new List<ChatContentPart>();
                        if (!string.IsNullOrEmpty(toolResult.Text))
                            parts.Add(ChatContentPart.TextPart(toolResult.Text));
                        parts.AddRange(toolResult.ImageDataUrls.Select(ChatContentPart.ImagePart));
                        toolMessage.ContentParts = parts;
                    }
                    working.Add(toolMessage);
                }

                // 本轮请求(助手+工具结果)结束后的检查:就地重算阈值,确保下一次请求的上下文不超限
                foreach (var registration in Plugins.EnabledPlugins)
                {
                    await registration.Plugin.OnAfterTurnAsync(this, session, working, runToken);
                }

                completedIndex = working.Count; // 本轮迭代完成:助手与全部工具结果成对,可安全写回
                Flush();
            }
        }
        catch (OperationCanceledException)
        {
            Flush(); // 中断:已完成迭代写回不丢(未完成的助手/工具不成对,不写,避免孤儿 tool_calls)
            _log.Info($"Agent run cancelled session={session.Id}");
            outcome = RunOutcome.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            Flush(); // 异常:已完成迭代同样保留
            _log.Error($"Agent run error session={session.Id}", ex);
            await callbacks.OnErrorAsync(session, ex, cancellationToken);
            outcome = RunOutcome.Failed(ex.Message);
            throw;
        }
        finally
        {
            s_activeCallbacks.Value = null; // 环外用量上报通道关闭(仅清本轮异步流,不波及并发运行)
            session.EndRun(); // 无论如何都释放请求锁
            // 运行结束钩子:三态结局都送达(插件内部已做异常隔离;此处兜底防止钩子分发本身出问题)
            try
            {
                await FireRunFinishedAsync(session, outcome).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"OnRunFinishedAsync dispatch failed session={session.Id}", ex);
            }
        }
    }

    /// <summary>组装本轮 system prompt(Agent 配置 + 各插件片段);需要自行构建消息列表的宿主可复用(如手动压缩)。
    /// 插件片段统一以 &lt;plugin name="插件名"&gt; 标签包裹后再拼接:多段连排时边界清晰,段归属一目了然;段内保持插件原文。</summary>
    public string BuildSystemPrompt(AgentSession session)
    {
        var systemPrompt = _options.SystemPrompt ?? "";
        foreach (var registration in Plugins.EnabledPlugins)
        {
            string? segment;
            try
            {
                segment = registration.Plugin.GetSystemPromptSegment(session);
            }
            catch (Exception ex)
            {
                // 片段故障不得拖垮整轮 prompt 组装:记日志后跳过该插件
                _log.Error($"Plugin {registration.Name} GetSystemPromptSegment failed (segment skipped): {ex}");
                continue;
            }
            if (string.IsNullOrEmpty(segment))
                continue;
            var wrapped = $"<plugin name=\"{registration.Plugin.Name}\">\n{segment}\n</plugin>";
            systemPrompt = string.IsNullOrEmpty(systemPrompt) ? wrapped : systemPrompt + "\n\n" + wrapped;
        }
        return systemPrompt;
    }

    /// <summary>组装本轮工作消息:合并 Agent 配置与插件贡献的 system prompt,追加队列插话与用户输入
    /// (返回输入消息实例与首条插话,供识别写回基线)。插话按 FIFO 拼在本轮输入之前:显式存量(宿主派发)
    /// 在前,会话内新到的在后。
    /// images/extraTexts 任一非空时用户消息以 content 数组发送:用户原话片段 + 每个附件一个独立 text 片段 + 图片片段。</summary>
    private List<ChatMessage> BuildWorkingMessages(AgentSession session, string userInput, List<string>? images, List<string>? extraTexts,
        List<QueuedUserMessage>? preQueued, out ChatMessage inputMessage, out ChatMessage? firstQueued)
    {
        var working = new List<ChatMessage>(session.Messages);

        var systemPrompt = BuildSystemPrompt(session);
        if (!string.IsNullOrEmpty(systemPrompt) && !working.Any(m => m.Role == ChatRole.System))
        {
            var systemMessage = new ChatMessage(ChatRole.System, systemPrompt);
            systemMessage.TokenCount = TokenEstimator.EstimateMessage(systemMessage);
            working.Insert(0, systemMessage);
        }

        inputMessage = BuildUserChatMessage(userInput, images, extraTexts);

        var pending = new List<ChatMessage>();
        if (preQueued is { Count: > 0 })
            pending.AddRange(preQueued.Select(BuildQueuedChatMessage));
        // stopAtDeferred:命令样与延迟条目不随消息拼入(空闲派发分别按命令/新轮处理),碰到即停
        var drained = session.DrainQueued(stopAtDeferred: true);
        pending.AddRange(drained.Select(BuildQueuedChatMessage));
        working.AddRange(pending);
        working.Add(inputMessage);
        firstQueued = pending.Count > 0 ? pending[0] : null;
        if (drained.Count > 0)
        {
            session.RaiseQueueDrained(drained, atRunStart: true);
            _log.Info($"Agent queue drained at run start: {drained.Count} message(s) spliced before input");
        }
        return working;
    }

    /// <summary>用户消息构造(本轮输入与队列插话共用):带附件时组装 content 多片段并估算 Token。</summary>
    private static ChatMessage BuildUserChatMessage(string text, List<string>? images, List<string>? extraTexts)
    {
        var message = new ChatMessage(ChatRole.User, text);
        if (images is { Count: > 0 } || extraTexts is { Count: > 0 })
        {
            // 多片段用户消息:用户原话(若有) + 每个附件一个独立 text 片段 + 图片片段,以 content 数组发送。
            // 文件内容不拼进原话:结构上隔离"用户说的话"与"文件内容",弱模型不易混淆边界
            var parts = new List<ChatContentPart>();
            if (!string.IsNullOrEmpty(text))
                parts.Add(ChatContentPart.TextPart(text));
            if (extraTexts is not null)
                parts.AddRange(extraTexts.Select(ChatContentPart.TextPart));
            if (images is not null)
                parts.AddRange(images.Select(ChatContentPart.FromAttachmentDataUrl));
            message.ContentParts = parts;
        }
        message.TokenCount = TokenEstimator.EstimateMessage(message);
        return message;
    }

    /// <summary>队列条目 → 用户消息:文本文件按普通发送同款格式拼为独立 text 片段;
    /// 消息 id 复用队列条目 id(宿主气泡 ClientId 与之对应,display↔context 匹配)。</summary>
    private static ChatMessage BuildQueuedChatMessage(QueuedUserMessage item)
    {
        var fileParts = item.TextFiles is { Count: > 0 }
            ? item.TextFiles.Select(f => "[Attached file: " + f.Name + "]\n" + f.Content).ToList()
            : null;
        var message = BuildUserChatMessage(item.Text, item.Images is { Count: > 0 } ? item.Images : null, fileParts);
        message.Id = item.Id;
        message.ClientId = item.Id;
        message.TimeCreated = DateTime.Now;
        return message;
    }

    /// <summary>执行一次流式模型请求:转发思考/内容增量,拼装完整的助手消息。</summary>
    private async Task<(ChatMessage Message, FinishReason? FinishReason, LLM.LlmUsage? Usage)> StreamTurnAsync(
        AgentSession session,
        List<ChatMessage> working,
        IAgentCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        var request = new ChatRequest
        {
            Messages = working,
            Tools = _tools.Count > 0 ? _tools.GetDefinitions() : null,
            Temperature = _options.Temperature,
        };

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolAccumulators = new Dictionary<int, ToolCallAccumulator>();
        var argsEmittedAt = new Dictionary<int, long>(); // 参数预览节流:accKey → 上次发出时刻(TickCount64)
        var argsEmitted = new Dictionary<int, int>();    // 参数预览去重:accKey → 上次发出时的快照长度
        int? lastArgsKey = null;                         // 最近收到参数增量的调用:用于调用切换/流结束时冲刷末态
        FinishReason? finishReason = null;
        LLM.LlmUsage? usage = null;

        // 助手消息在发起请求前生成并盖 id/创建时刻:流式回调期间宿主即可据此
        // 把当前轮次消息 id 盖到展示步骤上(display↔context 关联);内容在流结束后回填
        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Id = Guid.NewGuid().ToString("N"),
            TimeCreated = DateTime.Now,
        };
        var clientOptions = (session.LlmClient as OpenAiCompatibleClient)?.Options;
        if (clientOptions is not null) // 模型归因:会话连接自带供应商/模型(换模型后下一轮请求自动换成新值)
        {
            message.Provider = clientOptions.Provider;
            message.Model = clientOptions.Model;
        }
        await callbacks.OnTurnStartedAsync(session, message, cancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? genStart = null; // 首个流式 delta 时刻:TPS 的分母只算"生成时长",不含连接与首 token 等待
        await foreach (var evt in session.LlmClient.StreamAsync(request, cancellationToken))
        {
            switch (evt)
            {
                case ThinkingDeltaEvent thinking:
                    if (genStart is null) genStart = sw.Elapsed;
                    var thinkingContext = new StreamDeltaContext(StreamDeltaKind.Thinking, thinking.Text, evt);
                    InvokeOnStreamDelta(session, thinkingContext);
                    reasoning.Append(thinkingContext.Text);
                    await callbacks.OnThinkingDeltaAsync(session, thinkingContext.Text, cancellationToken);
                    break;

                case ContentDeltaEvent contentDelta:
                    if (genStart is null) genStart = sw.Elapsed;
                    var contentContext = new StreamDeltaContext(StreamDeltaKind.Content, contentDelta.Text, evt);
                    InvokeOnStreamDelta(session, contentContext);
                    content.Append(contentContext.Text);
                    await callbacks.OnContentDeltaAsync(session, contentContext.Text, cancellationToken);
                    break;

                case ToolCallDeltaEvent toolDelta:
                    InvokeOnStreamDelta(session, new StreamDeltaContext(StreamDeltaKind.ToolCall, toolDelta.ArgumentsDelta ?? "", evt));
                    // 归并键:index 是协议主键;个别端点缺省时按 id/名兜底,避免多个无 index 调用互相串参
                    var accKey = toolDelta.Index >= 0
                        ? toolDelta.Index
                        : (toolDelta.Id ?? toolDelta.Name ?? "\u0001").GetHashCode();
                    if (!toolAccumulators.TryGetValue(accKey, out var acc))
                    {
                        acc = new ToolCallAccumulator();
                        toolAccumulators[accKey] = acc;
                    }
                    if (toolDelta.Id is not null)
                        acc.Id = toolDelta.Id;
                    if (toolDelta.Name is not null)
                        acc.Name = toolDelta.Name;
                    acc.Arguments.Append(toolDelta.ArgumentsDelta);
                    // 调用切换即冲刷上一个调用的末态快照:它的参数已写完,若剩余内容落在 100ms
                    // 节流窗口内,常规节流不会再为它发帧——占位行行数统计会一直缺到执行开始
                    if (lastArgsKey is { } prevKey && prevKey != accKey)
                        await FlushArgsPreviewAsync(prevKey);
                    lastArgsKey = accKey;
                    // 参数流式预览(节流 ~100ms/调用,快照无增长不发):宿主可把"正在生成哪个文件的
                    // 编辑 +N/−N"提前亮出来;发快照而非增量,接收方幂等打补丁。参数写完的末态
                    // 由调用切换/流结束时的冲刷补发——紧随其后的 OnToolCallStartedAsync 是权威完整行
                    if (acc.Arguments.Length > 0 && acc.Arguments.Length != argsEmitted.GetValueOrDefault(accKey))
                    {
                        var now = Environment.TickCount64;
                        if (now - argsEmittedAt.GetValueOrDefault(accKey) >= 100)
                        {
                            argsEmittedAt[accKey] = now;
                            argsEmitted[accKey] = acc.Arguments.Length;
                            await callbacks.OnToolCallArgsDeltaAsync(session, accKey,
                                acc.Id.Length > 0 ? acc.Id : null,
                                acc.Name.Length > 0 ? acc.Name : null,
                                acc.Arguments.ToString(), cancellationToken);
                        }
                    }
                    break;

                case FinishedEvent finished:
                    InvokeOnStreamDelta(session, new StreamDeltaContext(StreamDeltaKind.Finished, "", evt));
                    finishReason = finished.Reason;
                    break;

                case UsageEvent usageEvent:
                    // TPS(最新):completion tokens / 生成时长(首 delta→usage 帧);未产生 delta 则不计时。
                    // 官方文档:completion_tokens 已包含 reasoning_tokens,叠加会虚高,不得再计
                    var emitted = usageEvent.CompletionTokens;
                    var tps = genStart is { } gs
                        && sw.Elapsed > gs
                        ? emitted / (sw.Elapsed - gs).TotalSeconds
                        : 0;
                    usage = new LLM.LlmUsage(usageEvent.PromptTokens, usageEvent.CompletionTokens, usageEvent.TotalTokens, usageEvent.CachedTokens, tps, usageEvent.ReasoningTokens);
                    await callbacks.OnUsageReportedAsync(session, usage, cancellationToken);
                    break;
            }
        }

        // 流结束:冲刷最后一个调用的参数末态(它的写完时刻之后可能再无该调用增量)
        if (lastArgsKey is { } finalKey)
            await FlushArgsPreviewAsync(finalKey);

        message.Content = content.Length > 0 ? content.ToString() : null;
        message.ReasoningContent = reasoning.Length > 0 ? reasoning.ToString() : null;
        message.TimeCompleted = DateTime.Now;
        message.Finish = FinishWireName(finishReason);
        if (usage is not null)
            // 逐消息真实用量落盘(reasoning 单列留档:completion 已含它,只作单独查看,不参与任何累计)
            message.Usage = new MessageUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, usage.CachedTokens, usage.ReasoningTokens);

        if (toolAccumulators.Count > 0)
        {
            message.ToolCalls = toolAccumulators
                .OrderBy(kv => kv.Key)
                .Select(kv => new ToolCall
                {
                    Id = kv.Value.Id,
                    Name = kv.Value.Name,
                    ArgumentsJson = kv.Value.Arguments.ToString(),
                })
                .ToList();
        }

        return (message, finishReason, usage);

        // 参数预览末态冲刷:绕过 100ms 节流把该调用当前完整快照发出(快照无增长则跳过)。
        // 仅本地函数:依赖本轮流式的 toolAccumulators/节流字典,跨轮并发互不可见
        async Task FlushArgsPreviewAsync(int key)
        {
            if (!toolAccumulators.TryGetValue(key, out var fa) || fa.Arguments.Length == 0)
                return;
            if (fa.Arguments.Length == argsEmitted.GetValueOrDefault(key))
                return;
            argsEmittedAt[key] = Environment.TickCount64;
            argsEmitted[key] = fa.Arguments.Length;
            await callbacks.OnToolCallArgsDeltaAsync(session, key,
                fa.Id.Length > 0 ? fa.Id : null,
                fa.Name.Length > 0 ? fa.Name : null,
                fa.Arguments.ToString(), cancellationToken);
        }
    }

    /// <summary>结束原因 → 落盘形态(主流约定的下划线命名)。</summary>
    private static string? FinishWireName(FinishReason? reason) => reason switch
    {
        FinishReason.Stop => "stop",
        FinishReason.ToolCalls => "tool_calls",
        FinishReason.Length => "length",
        FinishReason.ContentFilter => "content_filter",
        FinishReason.Other => "other",
        _ => null,
    };

    /// <summary>把流式增量交给启用的插件:插件可修改文本增量或抛异常强行中断。</summary>
    private void InvokeOnStreamDelta(AgentSession session, StreamDeltaContext context)
    {
        foreach (var registration in Plugins.EnabledPlugins)
            registration.Plugin.OnStreamDelta(this, session, context);
    }

    private sealed class ToolCallAccumulator
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }
}
