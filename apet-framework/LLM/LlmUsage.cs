namespace APet.Framework.LLM;

/// <summary>
/// 一次 LLM 请求的 Token 用量(来自响应 usage 字段;prompt_tokens 即当前上下文实际占用)。
/// CachedTokens:命中 Prompt 缓存的 prompt token 数(usage.prompt_tokens_details.cached_tokens,
/// OpenAI 兼容端点普遍提供)——底部状态栏的"缓存命中率"= 会话累计 cached / 累计 prompt。
/// ReasoningTokens:思考(reasoning)token 数(usage.completion_tokens_details.reasoning_tokens 原值留档;
/// 官方文档:已包含在 completion_tokens 内——只作逐消息单独查看,不参与输出累计/计费/TPS,叠加即虚高)。
/// Tps:本次请求的输出速度(completion tokens / 首个流式 delta 到结束的生成时长),
/// 状态栏"TPS(最新)"与逐消息落盘消费;0 = 未计时(非流式或空回复)。
/// </summary>
public sealed record LlmUsage(int PromptTokens, int CompletionTokens, int TotalTokens, int CachedTokens = 0, double Tps = 0, int ReasoningTokens = 0);
