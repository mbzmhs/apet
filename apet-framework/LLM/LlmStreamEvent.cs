using APet.Framework.Models;

namespace APet.Framework.LLM;

/// <summary>流式对话过程中的增量事件。</summary>
public abstract record LlmStreamEvent;

/// <summary>思考内容增量(reasoning_content)。</summary>
public sealed record ThinkingDeltaEvent(string Text) : LlmStreamEvent;

/// <summary>正文内容增量。</summary>
public sealed record ContentDeltaEvent(string Text) : LlmStreamEvent;

/// <summary>工具调用增量,由接收方按 Index 拼装为完整的 ToolCall。</summary>
public sealed record ToolCallDeltaEvent(int Index, string? Id, string? Name, string? ArgumentsDelta) : LlmStreamEvent;

/// <summary>本轮输出结束。</summary>
public sealed record FinishedEvent(FinishReason Reason) : LlmStreamEvent;

/// <summary>Token 用量上报(响应 usage 字段;流式经 stream_options.include_usage 在尾部块携带;
/// ReasoningTokens 来自 completion_tokens_details 原值留档——已含在 CompletionTokens 内,不参与计速)。</summary>
public sealed record UsageEvent(int PromptTokens, int CompletionTokens, int TotalTokens, int CachedTokens = 0, int ReasoningTokens = 0) : LlmStreamEvent;
