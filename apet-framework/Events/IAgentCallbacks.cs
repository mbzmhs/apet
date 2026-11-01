using APet.Framework.Agent;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;

namespace APet.Framework.Events;

/// <summary>Agent 消息回调。整个运行生命周期内都会携带当前的 AgentSession。</summary>
public interface IAgentCallbacks
{
    /// <summary>
    /// 一次模型请求即将发起:assistantMessage 已生成(含消息 id 与创建时刻),其内容/工具调用将在流式结束后回填。
    /// 宿主据此把当前轮次消息 id 盖到展示步骤上(display↔context 关联)。
    /// </summary>
    Task OnTurnStartedAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default);

    /// <summary>流式:思考内容增量。</summary>
    Task OnThinkingDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default);

    /// <summary>流式:正文内容增量。</summary>
    Task OnContentDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default);

    /// <summary>工具调用即将执行。</summary>
    Task OnToolCallStartedAsync(AgentSession session, ToolCall toolCall, CancellationToken cancellationToken = default);

    /// <summary>
    /// 工具调用参数流式增量(已按调用节流 ~100ms):index/argsKey 为归并键,callId/toolName
    /// 可能在流早期为 null;argsSnapshot 是累积快照而非增量(接收方幂等打补丁,不怕乱序/丢帧)。
    /// 供宿主在执行开始前显示"正在生成哪个文件的编辑"(半截 JSON 尽力解析);默认无需实现。
    /// </summary>
    Task OnToolCallArgsDeltaAsync(AgentSession session, int argsKey, string? callId, string? toolName, string argsSnapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// 工具执行过程中的实时输出增量(如运行终端工具的 stdout/stderr),供宿主边跑边显示。
    /// 工具经 ToolExecutionScope.Output 推送,宿主可按需节流;未实现时输出只在工具结束时回传。
    /// </summary>
    Task OnToolCallOutputAsync(AgentSession session, ToolCall toolCall, string stream, string delta, CancellationToken cancellationToken = default);

    /// <summary>工具调用执行完成,result 为回传给 LLM 的完整结果(文本 + 可选图片附件)。</summary>
    Task OnToolCallFinishedAsync(AgentSession session, ToolCall toolCall, ToolResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// 工具调用鉴权回调:安全策略未放行(只读目录写入、效果未知等)时调用,
    /// 宿主决定放行(返回 Allow)或拒绝(返回 Deny);默认实现为拒绝。
    /// </summary>
    Task<AuthorizationResult> OnToolCallAuthorizationAsync(
        AgentSession session,
        ToolCall toolCall,
        ToolEffect effect,
        AuthorizationResult decision,
        CancellationToken cancellationToken = default);

    /// <summary>本轮运行完成,新消息已写入 Session。</summary>
    Task OnCompletedAsync(AgentSession session, AgentResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// 一条助手消息组装完成(含工具调用轮),随即写入运行工作集。
    /// 供宿主做"等待子代理对插话的下一条回复"这类按消息粒度的同步;每轮必发一次。
    /// </summary>
    Task OnAssistantMessageAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default);

    /// <summary>本轮运行出错,异常随后会重新抛出。</summary>
    Task OnErrorAsync(AgentSession session, Exception exception, CancellationToken cancellationToken = default);

    /// <summary>每次模型请求返回 Token 用量(prompt_tokens 即当前上下文实际占用)。</summary>
    Task OnUsageReportedAsync(AgentSession session, LLM.LlmUsage usage, CancellationToken cancellationToken = default);
}

/// <summary>空实现,宿主按需重写关心的回调即可。</summary>
public abstract class AgentCallbacks : IAgentCallbacks
{
    public virtual Task OnTurnStartedAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnThinkingDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnContentDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnToolCallStartedAsync(AgentSession session, ToolCall toolCall, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnToolCallArgsDeltaAsync(AgentSession session, int argsKey, string? callId, string? toolName, string argsSnapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnToolCallOutputAsync(AgentSession session, ToolCall toolCall, string stream, string delta, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnToolCallFinishedAsync(AgentSession session, ToolCall toolCall, ToolResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task<AuthorizationResult> OnToolCallAuthorizationAsync(
        AgentSession session,
        ToolCall toolCall,
        ToolEffect effect,
        AuthorizationResult decision,
        CancellationToken cancellationToken = default) => Task.FromResult(decision);

    public virtual Task OnCompletedAsync(AgentSession session, AgentResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnAssistantMessageAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnErrorAsync(AgentSession session, Exception exception, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public virtual Task OnUsageReportedAsync(AgentSession session, LLM.LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
