using APet.Framework.Agent;
using APet.Framework.Events;
using APet.Framework.LLM;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;

namespace APet.Plugins.SubAgent;

/// <summary>
/// 子代理运行的流式回调:事件帧转发给宿主事件出口(模态框/侧边栏经 SSE 消费)并记入运行帧日志;
/// 工具鉴权未放行时交给宿主处理(卡片投到主会话,用户在主聊天窗裁决)。
/// 事件载荷刻意保持自包含(不依赖 UI 层展示格式化),模态框按此协议渲染。
/// </summary>
public sealed class SubAgentCallbacks : AgentCallbacks
{
    private readonly SubAgentPlugin _plugin;
    private readonly SubAgentRun _run;
    private readonly string? _workdir;

    public SubAgentCallbacks(SubAgentPlugin plugin, SubAgentRun run, string? workdir = null)
    {
        _plugin = plugin;
        _run = run;
        _workdir = workdir;
    }

    /// <summary>接收本运行每次请求的用量(插件接宿主注入的记账出口);未设置时用量只进帧日志。</summary>
    internal Action<LlmUsage>? OnUsage { get; set; }

    public override Task OnThinkingDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default)
    {
        _run.Publish("thinking_delta", new { delta });
        return Task.CompletedTask;
    }

    public override Task OnContentDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default)
    {
        _run.Publish("content_delta", new { delta });
        return Task.CompletedTask;
    }

    public override Task OnToolCallStartedAsync(AgentSession session, ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        _run.Publish("tool_started", BuildToolFrame(toolCall.Name, toolCall.ArgumentsJson, null, false));
        return Task.CompletedTask;
    }

    public override Task OnToolCallFinishedAsync(AgentSession session, ToolCall toolCall, ToolResult result, CancellationToken cancellationToken = default)
    {
        // 工具管线错误统一为 {"error":…} JSON 文本(与主聊天判定同源;格式化帧另有展示层判定)
        var isError = result.Text.StartsWith("{\"error\":", StringComparison.Ordinal);
        _run.Publish("tool_finished", BuildToolFrame(toolCall.Name, toolCall.ArgumentsJson, result.Text, isError));
        return Task.CompletedTask;
    }

    /// <summary>工具执行中的实时输出增量(如运行终端工具的 stdout/stderr):帧小、频率受工具侧节流控制;
    /// 子代理帧不带调用 id,模态框按"运行中的同名工具行"落位。</summary>
    public override Task OnToolCallOutputAsync(AgentSession session, ToolCall toolCall, string stream, string delta, CancellationToken cancellationToken = default)
    {
        if (delta.Length > 0)
            _run.Publish("tool_output", new { toolName = toolCall.Name, stream, delta });
        return Task.CompletedTask;
    }

    /// <summary>工具帧载荷:宿主注入了展示格式化器时走它(与主聊天 SSE 帧同构);否则携带原始 args/result 文本。</summary>
    private object BuildToolFrame(string name, string args, string? result, bool rawError)
    {
        var display = DisplayName(name);
        if (_plugin.ToolFrameFormatter is { } formatter)
        {
            try
            {
                var formatted = formatter(name, display, args, result, rawError, _workdir);
                if (formatted is not null)
                    return formatted;
            }
            catch
            {
                // 格式化异常回退原始帧:观测链路不因展示格式化失败而断
            }
        }
        return result is null
            ? new { toolName = name, displayName = display, args }
            : new { toolName = name, displayName = display, result, isError = rawError };
    }

    public override async Task<AuthorizationResult> OnToolCallAuthorizationAsync(
        AgentSession session,
        ToolCall toolCall,
        ToolEffect effect,
        AuthorizationResult decision,
        CancellationToken cancellationToken = default)
    {
        if (decision.Allowed || _plugin.AuthHandler is not { } handler)
            return decision; // 宿主未接线时按策略判定拒绝(不虚放)
        try
        {
            return await handler(
                new SubAgentAuthContext(_run.Id, _run.Name, ToolExecutionScope.SessionId, toolCall, effect, decision),
                cancellationToken);
        }
        catch
        {
            return AuthorizationResult.Deny("authorization handler failed");
        }
    }

    public override Task OnCompletedAsync(AgentSession session, AgentResult result, CancellationToken cancellationToken = default)
    {
        _run.Publish("complete", null);
        return Task.CompletedTask;
    }

    /// <summary>每条助手消息组装完成:转给运行对象,在途传话的下一条非空正文即"回复"(sub_tell 的等待点)。</summary>
    public override Task OnAssistantMessageAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default)
    {
        _run.OnAssistantMessage(assistantMessage);
        return Task.CompletedTask;
    }

    public override Task OnErrorAsync(AgentSession session, Exception exception, CancellationToken cancellationToken = default)
    {
        _run.Publish("error", new { message = exception.Message });
        return Task.CompletedTask;
    }

    public override Task OnUsageReportedAsync(AgentSession session, LlmUsage usage, CancellationToken cancellationToken = default)
    {
        _run.Publish("usage", new { promptTokens = usage.PromptTokens });
        OnUsage?.Invoke(usage); // 子代理用量交宿主记账(累计进发起会话;压缩摘要的用量同路径)
        return Task.CompletedTask;
    }

    private string DisplayName(string toolName) =>
        _plugin.DisplayNames?.Invoke(toolName) ?? toolName;
}
