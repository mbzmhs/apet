namespace APet.Plugins.BiliLive;

/// <summary>直播间事件类型。</summary>
public enum LiveKind
{
    Danmaku,  // 聊天弹幕(DANMU_MSG)
    Gift,     // 礼物(SEND_GIFT)
    Sc,       // 醒目留言(SUPER_CHAT_MESSAGE)
    Interact, // 互动(INTERACT_WORD_V2:关注/特别关注/互粉/分享;进场不响应)
}

/// <summary>一条已解析的直播间事件(值语义,跨线程安全)。Danmaku=弹幕内容;Gift=「礼物名 xN」;Sc=留言内容;Interact=动作描述。</summary>
public sealed record LiveEvent(LiveKind Kind, string User, long Mid, string Text, double PriceYuan);

/// <summary>过滤规则:类型开关 → 屏蔽用户 → 价格阈值 → 关键词。</summary>
internal sealed class LiveFilter
{
    public bool RespondDanmaku { get; init; } = true;
    public bool RespondGift { get; init; } = true;
    public bool RespondSc { get; init; } = true;
    public bool RespondInteract { get; init; } = true;
    public double MinGiftPrice { get; init; }
    public double MinScPrice { get; init; }
    public HashSet<string> BlockKeywords { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> BlockUsers { get; init; } = new();

    /// <summary>true=放行。</summary>
    public bool Pass(LiveEvent e)
    {
        switch (e.Kind)
        {
            case LiveKind.Danmaku: if (!RespondDanmaku) return false; break;
            case LiveKind.Gift:
                if (!RespondGift) return false;
                if (e.PriceYuan < MinGiftPrice) return false;
                break;
            case LiveKind.Sc:
                if (!RespondSc) return false;
                if (e.PriceYuan < MinScPrice) return false;
                break;
            case LiveKind.Interact: if (!RespondInteract) return false; break;
        }
        if (e.Mid > 0 && BlockUsers.Contains(e.Mid)) return false;
        foreach (var kw in BlockKeywords)
            if (e.User.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                e.Text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}

/// <summary>事件 → 工具结果文本的格式化(明确标注直播间来源,标签用英文,观众内容原样保留)。</summary>
internal static class LiveFormat
{
    /// <summary>把一批事件格式化为工具结果文本:连续弹幕合并编号(每批最多 maxMergeItems 条),其余逐条一行。</summary>
    public static string FormatEvents(IReadOnlyList<LiveEvent> events, int maxMergeItems)
    {
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < events.Count)
        {
            if (events[i].Kind == LiveKind.Danmaku)
            {
                var batch = new List<LiveEvent>();
                while (i < events.Count && events[i].Kind == LiveKind.Danmaku && batch.Count < maxMergeItems)
                    batch.Add(events[i++]);
                if (batch.Count == 1)
                {
                    sb.AppendLine(FormatDanmaku(batch[0]));
                }
                else
                {
                    sb.Append("[BiliLive] Danmaku (").Append(batch.Count).AppendLine("):");
                    for (var k = 0; k < batch.Count; k++)
                        sb.Append(k + 1).Append(". ").AppendLine(FormatDanmaku(batch[k], withPrefix: false));
                }
                continue;
            }

            var e = events[i++];
            sb.AppendLine(e.Kind switch
            {
                LiveKind.Gift => $"[BiliLive] Gift: viewer 「{e.User}」 sent {e.Text} ({Money(e.PriceYuan)})",
                LiveKind.Sc => $"[BiliLive] SC: viewer 「{e.User}」 said 「{e.Text}」 ({Money(e.PriceYuan)})",
                LiveKind.Interact => $"[BiliLive] Interaction: viewer 「{e.User}」 {e.Text}",
                _ => FormatDanmaku(e),
            });
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatDanmaku(LiveEvent e, bool withPrefix = true) =>
        (withPrefix ? "[BiliLive] Danmaku from " : "")
        + (string.IsNullOrEmpty(e.User) ? "an anonymous viewer" : $"viewer 「{e.User}」")
        + $": 「{e.Text}」";

    /// <summary>价格显示:整数不带小数(¥5),非整数一位小数(¥0.5)。</summary>
    public static string Money(double yuan) =>
        Math.Abs(yuan - Math.Round(yuan)) < 0.01 ? "¥" + ((long)Math.Round(yuan)).ToString("N0") : "¥" + yuan.ToString("0.0");
}
