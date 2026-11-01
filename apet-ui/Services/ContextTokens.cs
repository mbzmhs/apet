using APet.Framework.Models;

namespace APet.UI.Services;

/// <summary>
/// 上下文 Token 估算与格式化:估算逻辑在框架 TokenEstimator(中文约 1 字/token,其他约 4 字符/token),
/// 消息进入会话时已把估算值记录到 ChatMessage.TokenCount,这里优先读记录值。
/// </summary>
public static class ContextTokens
{
    public static int Estimate(string text) => TokenEstimator.Estimate(text);

    public static int EstimateMessages(IEnumerable<ChatMessage> messages) =>
        messages.Sum(TokenEstimator.TokenCount);

    /// <summary>856 → "856";23456 → "23.5K";1000000 → "1.0M";1234567 → "1.23M"。</summary>
    public static string Format(int tokens)
    {
        return tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:0.0#}M",
            >= 1_000 => $"{tokens / 1_000.0:0.#}K",
            _ => tokens + " tok",
        };
    }
}
