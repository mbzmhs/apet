namespace APet.Framework.Models;

/// <summary>
/// 上下文 Token 启发式估算:中文约 1 字/token,其他约 4 字符/token,另计每条固定开销。
/// 用于压缩触发判断与保留窗口累计,非精确分词结果;消息进入会话时把估算值记录到
/// ChatMessage.TokenCount,后续直接读记录值,避免重复扫描文本。
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string text)
    {
        int cjk = 0, other = 0;
        foreach (var ch in text ?? "")
        {
            if (ch > 0x2E80)
                cjk++;
            else
                other++;
        }
        return cjk + other / 4;
    }

    /// <summary>单条消息的 Token 占用:正文 + 思考 + 工具调用参数,另计固定开销。
    /// ContentParts 非空时按 wire 格式计 text 片段之和(此时 Content 不再发送,避免漏计附件全文)。</summary>
    public static int EstimateMessage(ChatMessage message)
    {
        var body = message.ContentParts is { Count: > 0 }
            ? string.Concat(message.ContentParts.Where(p => p.Type == "text").Select(p => p.Text ?? ""))
            : message.Content ?? "";
        var total = Estimate(body + (message.ReasoningContent ?? "")) + 4;
        if (message.ToolCalls is not null)
            foreach (var call in message.ToolCalls)
                total += (call.ArgumentsJson?.Length ?? 0) / 4 + 8;
        return total;
    }

    /// <summary>消息 Token 占用:优先取对话时记录的值,未记录(如旧会话文件恢复)则即时估算。</summary>
    public static int TokenCount(ChatMessage message) =>
        message.TokenCount > 0 ? message.TokenCount : EstimateMessage(message);
}
