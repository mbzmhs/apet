namespace APet.Framework.Models;

/// <summary>非流式请求的完整响应。</summary>
public sealed class ChatResponse
{
    public required ChatMessage Message { get; init; }

    /// <summary>Token 用量(服务端提供时)。</summary>
    public APet.Framework.LLM.LlmUsage? Usage { get; init; }

    public FinishReason FinishReason { get; init; }
}
