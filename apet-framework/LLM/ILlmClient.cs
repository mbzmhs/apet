using APet.Framework.Models;

namespace APet.Framework.LLM;

/// <summary>LLM 通讯抽象,默认实现为 OpenAI 兼容协议客户端。</summary>
public interface ILlmClient
{
    /// <summary>非流式对话,一次性返回完整响应。</summary>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>流式对话,逐段产出增量事件(思考/内容/工具调用/结束)。</summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
