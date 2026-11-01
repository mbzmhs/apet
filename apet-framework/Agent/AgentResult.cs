using APet.Framework.Models;

namespace APet.Framework.Agent;

/// <summary>一轮 Agent 运行的结果。</summary>
public sealed class AgentResult
{
    /// <summary>最终回复文本。</summary>
    public string? Content { get; init; }

    /// <summary>最终 Assistant 消息(含思考内容、工具调用等原始信息)。</summary>
    public required ChatMessage FinalMessage { get; init; }

    /// <summary>本轮新产生的全部消息(user/assistant/tool),与写入 Session 的内容一致。</summary>
    public required IReadOnlyList<ChatMessage> NewMessages { get; init; }

    /// <summary>最后一次模型请求的 Token 用量(服务端提供时;prompt_tokens 即上下文占用)。</summary>
    public LLM.LlmUsage? Usage { get; init; }
}
