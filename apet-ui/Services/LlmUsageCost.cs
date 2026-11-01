using APet.Framework.LLM;

namespace APet.UI.Services;

/// <summary>
/// 逐请求计费(元):计费因子在设置页的模型条目配置(元/百万 tokens,0 = 该项不计费)。
/// 口径:非缓存输入×PriceInput + 缓存输入×PriceInputCached + 输出×PriceOutput。
/// 官方文档:completion_tokens 已包含 reasoning_tokens,思考不另计费、不得叠加。
/// </summary>
public static class LlmUsageCost
{
    /// <summary>查 供应商/模型 的计费因子;供应商回退到激活项,模型未配置 = 全 0(免费)。</summary>
    public static (double PriceInput, double PriceInputCached, double PriceOutput) PriceOf(
        AgentConfig config, string? provider, string? model)
    {
        var prov = (provider is { Length: > 0 } ? config.Llm.Find(provider) : null) ?? config.Llm.Active;
        if (prov is null) return (0, 0, 0);
        var m = model is { Length: > 0 }
            ? prov.Models.FirstOrDefault(x => string.Equals(x.Name, model, StringComparison.OrdinalIgnoreCase))
            : null;
        return m is null
            ? (0, 0, 0)
            : (m.PriceInput, m.PriceInputCached, m.PriceOutput);
    }

    /// <summary>按给定计费因子算本次请求费用(元)。</summary>
    public static double Calc(LlmUsage usage, double priceInput, double priceInputCached, double priceOutput)
    {
        var nonCached = Math.Max(0, usage.PromptTokens - usage.CachedTokens);
        return nonCached / 1_000_000d * priceInput
             + Math.Min(usage.CachedTokens, usage.PromptTokens) / 1_000_000d * priceInputCached
             + usage.CompletionTokens / 1_000_000d * priceOutput;
    }

    /// <summary>按会话当前 供应商/模型 算本次请求费用(元)。</summary>
    public static double Of(AgentConfig config, string? provider, string? model, LlmUsage usage)
    {
        var (a, b, c) = PriceOf(config, provider, model);
        return Calc(usage, a, b, c);
    }
}
