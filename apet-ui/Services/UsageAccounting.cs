using APet.Framework.LLM;
using APet.UI.ViewModels;

namespace APet.UI.Services;

/// <summary>逐请求用量记账:累计会话总用量与费用(主循环回调链与手动压缩共用,口径一致)。</summary>
public static class UsageAccounting
{
    /// <summary>把本次请求的用量应用到会话累计统计。
    /// billingProvider/billingModel:本次请求实际使用的供应商/模型锚定(轮次起始捕获;连接按轮固定,
    /// 轮内切换只影响下一轮)。留空 = 回退会话当前选择(空闲手动压缩:连接与当前选择一致,无错位)。
    /// config 为 null 时只累计 token 数、不累计费用。</summary>
    public static void Apply(SessionViewModel session, AgentConfig? config, LlmUsage usage,
        string? billingProvider = null, string? billingModel = null)
    {
        var cost = config is null
            ? 0
            : LlmUsageCost.Of(config,
                billingProvider is { Length: > 0 } ? billingProvider : session.ChatProvider,
                billingModel is { Length: > 0 } ? billingModel : session.ChatModel,
                usage);
        session.AddUsage(usage, cost);
    }
}
