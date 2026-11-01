using APet.Framework.LLM;
using APet.Framework.Models;
using APet.Framework.Session;
using AgentClass = APet.Framework.Agent.Agent;

namespace APet.Framework.Plugin;

/// <summary>一轮运行结局的三态。</summary>
public enum RunOutcomeKind
{
    /// <summary>本轮成功完成(消息已落盘)。</summary>
    Completed,
    /// <summary>用户中断(停止按钮/取消令牌;已收到的部分按中断语义处理)。</summary>
    Cancelled,
    /// <summary>运行出错(RunOutcome.Error 携带异常消息)。</summary>
    Failed,
}

/// <summary>
/// 一轮运行的结局:成功完成 / 用户中断 / 出错。经 OnRunFinishedAsync 交给插件——
/// 三个结局(含中断与异常路径)都会送达,可据此做收尾(TTS 播报完成、宠物反应、资源释放)。
/// </summary>
public sealed record RunOutcome
{
    public RunOutcomeKind Kind { get; init; }

    /// <summary>Kind=Failed 时的异常消息,其余为 null。</summary>
    public string? Error { get; init; }

    public static RunOutcome Completed { get; } = new() { Kind = RunOutcomeKind.Completed };
    public static RunOutcome Cancelled { get; } = new() { Kind = RunOutcomeKind.Cancelled };
    public static RunOutcome Failed(string error) => new() { Kind = RunOutcomeKind.Failed, Error = error };
}

/// <summary>
/// 会话生命周期事件。Created:新对话/分叉/从磁盘还原/移出归档——凡经 Agent.CreateSession
/// 活化的会话;Removed:删除/归档——由宿主在会话离开运行时前触发。
/// </summary>
public enum SessionEventKind
{
    Created,
    Removed,
}

/// <summary>流式增量的种类。</summary>
public enum StreamDeltaKind
{
    Thinking,
    Content,
    ToolCall,
    Finished,
}

/// <summary>
/// 流式增量事件上下文,插件可读取、修改文本(对思考/内容增量生效)或抛出异常强行中断本轮运行。
/// </summary>
public sealed class StreamDeltaContext
{
    public StreamDeltaContext(StreamDeltaKind kind, string text, LlmStreamEvent sourceEvent)
    {
        Kind = kind;
        Text = text;
        SourceEvent = sourceEvent;
    }

    public StreamDeltaKind Kind { get; }

    /// <summary>增量文本。Thinking/Content 增量可修改,修改结果会用于消息拼装与宿主回调;其余种类只读。</summary>
    public string Text { get; set; }

    /// <summary>原始流式事件。</summary>
    public LlmStreamEvent SourceEvent { get; }
}

/// <summary>
/// Agent 插件:比工具更高层的能力扩展,可参与系统提示词拼接、介入消息链
/// (请求前 / 流式增量 / 助手消息完成)查看与修改内容、抛出异常强行中断运行。
/// 消息链钩子中同时持有 Agent 与 AgentSession 的完整实例。
/// Initialize 与 Dispose 必须实现,由 Enabled 开关变化自动驱动。
/// </summary>
public interface IAgentPlugin
{
    /// <summary>插件名称,同一 Agent 内不允许重复注册。</summary>
    string Name { get; }

    /// <summary>
    /// 启用开关。约定通过 Agent.Plugins 中的注册对象(PluginRegistration.SetEnabled)切换,
    /// 切换时框架自动调用 Initialize / Dispose 并保持本属性同步。
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>启用时调用(Enabled false→true)。</summary>
    void Initialize();

    /// <summary>禁用时调用(Enabled true→false)。</summary>
    void Dispose();

    /// <summary>
    /// 是否把本插件作为工具暴露给 LLM(默认否)。
    /// 置为 true 后,AgentPluginBase 的 ToolDefinition/ExecuteToolAsync 必须一并重写;
    /// 工具定义随每次请求发送,模型调用时经 ToolRegistry 动态工具管线统一鉴权执行。
    /// </summary>
    bool IsTool => false;

    /// <summary>贡献一段 system prompt,Agent 会以 &lt;plugin name="插件名"&gt; 标签包裹后拼接到系统提示词之后(段内保持原文)。
    /// 返回 null 或空表示不贡献。</summary>
    string? GetSystemPromptSegment(AgentSession session) => null;

    /// <summary>插件注册的聊天框快捷指令(宿主以 $ 前缀提供补全与执行);默认无。</summary>
    IReadOnlyList<PluginChatCommand> GetChatCommands() => [];

    /// <summary>运行开始前(工作消息组装后、第一轮请求前),可异步重排/压缩上下文。</summary>
    Task OnBeforeRunAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages) => Task.CompletedTask;

    /// <summary>
    /// 每次 LLM 请求发出前(含工具循环中的每一轮):可查看、增删、修改待发送消息,
    /// 抛出异常将强行中断本次运行。
    /// </summary>
    void OnBeforeRequest(AgentClass agent, AgentSession session, List<ChatMessage> messages) { }

    /// <summary>
    /// 每次 LLM 请求发出前的异步钩子(含工具循环中的每一轮):可异步检查与调整待发送消息
    /// (如运行中触发上下文压缩),抛出异常将强行中断本次运行。默认无操作。
    /// </summary>
    Task OnBeforeRequestAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// 流式增量到达时(思考/内容/工具调用/结束):可读取或修改文本增量,抛出异常将强行中断。
    /// </summary>
    void OnStreamDelta(AgentClass agent, AgentSession session, StreamDeltaContext delta) { }

    /// <summary>
    /// 每轮模型输出组装完成时(执行工具或结束之前):可直接修改助手消息内容(含思考内容),
    /// 抛出异常将强行中断且该轮内容不会写入 Session。
    /// </summary>
    void OnAssistantMessage(AgentClass agent, AgentSession session, ChatMessage assistantMessage) { }

    /// <summary>
    /// 一次 LLM 请求(轮次)结束后:工具轮在助手消息与全部工具结果都进入工作消息后调用;
    /// 最后一轮在运行结果写回后调用(单轮运行也必触发)。可异步检查/调整上下文
    /// (如请求结束后重算压缩阈值、为下一次请求或运行收尾瘦身),抛出异常将强行中断本次运行。默认无操作。
    /// </summary>
    Task OnAfterTurnAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// 一轮运行结束时调用:成功/中断/出错三个结局都会触发(OnAfterTurnAsync 只在成功路径触发,
    /// 收尾类需求——TTS 播报完成、宠物反应、资源释放——请用本钩子)。运行时已告一段落,
    /// 此处抛出的异常不会回传(仅记日志),不得依赖它影响运行。
    /// </summary>
    Task OnRunFinishedAsync(AgentClass agent, AgentSession session, RunOutcome outcome) => Task.CompletedTask;

    /// <summary>
    /// 会话生命周期事件(Created/Removed;触发线程与实现方文档一致,不得阻塞)。
    /// 此处抛出的异常不会回传(仅记日志)。
    /// </summary>
    void OnSessionEvent(AgentClass agent, AgentSession session, SessionEventKind kind) { }
}
