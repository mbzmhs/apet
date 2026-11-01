using System.Text;
using System.Text.Json;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;

namespace APet.Plugins.Memory;

/// <summary>mem_* 工具公共实现:条目 Token 估算、按预算从旧到新截断、文本格式化与时间戳解析。</summary>
internal static class MemoryToolSupport
{
    /// <summary>单条记忆内容长度上限(字符);超长要求模型拆分记录。</summary>
    public const int MaxContentChars = 2000;

    /// <summary>mem_search 关键词个数上限。</summary>
    public const int MaxKeywords = 8;

    /// <summary>每条返回条目的元数据(id/日期/使用数)Token 开销估算。</summary>
    public const int EntryOverheadTokens = 24;

    public static int EstimateEntryTokens(MemoryEntry entry) =>
        TokenEstimator.Estimate(entry.Content) + EntryOverheadTokens;

    /// <summary>
    /// 按预算截断:输入须按"最近活动"倒序(最新在前),从最旧开始丢弃直到总 Token ≤ budget;
    /// 严格不超预算——连最新一条都放不下时 Kept 为空,由调用方提示模型收窄查询。
    /// </summary>
    public static (IReadOnlyList<MemoryEntry> Kept, int Total) FitToBudget(
        IReadOnlyList<MemoryEntry> byRecencyDesc, int budgetTokens)
    {
        var kept = new List<MemoryEntry>(byRecencyDesc.Count);
        var used = 0;
        foreach (var entry in byRecencyDesc)
        {
            var cost = EstimateEntryTokens(entry);
            if (used + cost > budgetTokens)
                break;
            kept.Add(entry);
            used += cost;
        }

        return (kept, byRecencyDesc.Count);
    }

    /// <summary>条目列表 → 给模型的文本(每条一行,比 JSON 省 Token)。</summary>
    public static string FormatList(IReadOnlyList<MemoryEntry> entries, int totalHits, string scope)
    {
        if (totalHits == 0)
            return $"No memories found ({scope}).";
        var sb = new StringBuilder();
        sb.Append(scope);
        sb.Append(entries.Count < totalHits
            ? $" — {entries.Count} of {totalHits} hits returned, older ones truncated to fit the context budget.\n"
            : " — all hits returned.\n");
        foreach (var entry in entries)
            sb.Append(FormatEntry(entry)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>单条 → 一行文本。</summary>
    public static string FormatEntry(MemoryEntry entry) =>
        $"#{entry.Id} | created {Local(entry.CreatedAt)} | used {entry.UseCount}x, last {Local(entry.LastUsedAt)} | {entry.Content}";

    /// <summary>Unix 秒 → 本地时间字符串。</summary>
    public static string Local(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// 解析模型给出的时间戳:Unix 秒/毫秒或 ISO 8601(如 2026-09-01、2026-09-01T14:30);
    /// 纯日期按本地时间解释,endOfDayIfDateOnly 为 true 时取当天最后一秒(供区间上界使用)。
    /// </summary>
    public static bool TryParseTime(string? raw, bool endOfDayIfDateOnly, out long unixSeconds)
    {
        unixSeconds = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var text = raw.Trim();

        if (long.TryParse(text, out var number))
        {
            // 13 位视为毫秒
            unixSeconds = number > 100_000_000_000L ? number / 1000 : number;
            return true;
        }

        if (DateTimeOffset.TryParse(text, out var dto))
        {
            unixSeconds = dto.ToUnixTimeSeconds();
            if (endOfDayIfDateOnly && text.Length == 10) // "yyyy-MM-dd":上界含当天全天
                unixSeconds += 86_399;
            return true;
        }

        return false;
    }

    /// <summary>关键词切分:空白/逗号分隔,去重,截断到 MaxKeywords。</summary>
    public static IReadOnlyList<string> SplitKeywords(string raw) =>
        (raw ?? "")
            .Split([' ', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeywords)
            .ToList();

    /// <summary>content 命中的关键词个数(OR 搜索的相关性排序用:命中越多越相关)。</summary>
    public static int CountKeywordHits(string content, IReadOnlyList<string> keywords) =>
        keywords.Count(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>解析 argumentsJson 中的字符串字段;失败抛 ArgumentException(由注册中心转错误 JSON)。</summary>
    public static string GetRequiredString(string argumentsJson, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.GetProperty(field).GetString() ?? "";
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
    }

    /// <summary>解析 argumentsJson 中的整数字段。</summary>
    public static long GetRequiredInt(string argumentsJson, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.GetProperty(field).GetInt64();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
    }

    /// <summary>
    /// 查询类工具的统一收尾:遗忘清扫 → 取全部命中 → 按实时预算从尾部截断 → 格式化。
    /// recencyOrder 指定"保留优先序"(排前的先留,默认按最近活动倒序);
    /// chronological=true 时保留条目改按创建时间正序显示(mem_range)。
    /// </summary>
    public static string QueryWithBudget(
        MemoryPlugin plugin,
        Func<IReadOnlyList<MemoryEntry>> query,
        string scope,
        bool chronological = false,
        Func<IReadOnlyList<MemoryEntry>, IReadOnlyList<MemoryEntry>>? recencyOrder = null)
    {
        plugin.ForgetSweep();
        var all = query();
        var budget = plugin.ComputeBudget(ToolExecutionScope.ContextUsage);
        var byRecencyDesc = recencyOrder?.Invoke(all)
            ?? all.OrderByDescending(e => Math.Max(e.CreatedAt, e.LastUsedAt)).ToList();
        var (kept, total) = FitToBudget(byRecencyDesc, budget);

        if (total > 0 && kept.Count == 0)
            return $"No memories returned ({scope}): even the newest single hit exceeds the context budget. " +
                   "Narrow the time range or keywords and try again.";
        if (chronological)
            kept = kept.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToList();
        return FormatList(kept, total, scope);
    }
}

/// <summary>mem_record:把一段内容记入长期记忆。</summary>
public sealed class MemRecordTool : ToolBase
{
    private readonly MemoryPlugin _plugin;

    public MemRecordTool(MemoryPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "mem_record",
        "Save a fact, preference, decision, or event to this agent's long-term memory " +
        "(shared across all sessions, persisted on disk). Use when the user asks you to remember something, " +
        "or when durable personal context emerges. Returns the new memory id.",
        """
        {
          "type": "object",
          "properties": {
            "content": { "type": "string", "description": "The memory text; self-contained, at most 2000 characters" }
          },
          "required": ["content"]
        }
        """,
        displayName: "记录记忆");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect; // 内部存储,非用户文件

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var content = MemoryToolSupport.GetRequiredString(argumentsJson, "content").Trim();
        if (content.Length == 0)
            throw new ArgumentException("content must not be empty.");
        if (content.Length > MemoryToolSupport.MaxContentChars)
            throw new ArgumentException($"content exceeds {MemoryToolSupport.MaxContentChars} characters; split it into several shorter memories.");

        _plugin.ForgetSweep();
        var id = _plugin.Store.Record(content);
        return Task.FromResult("Recorded memory #" + id + " at " + MemoryToolSupport.Local(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + ".");
    }
}

/// <summary>mem_recent:最近 n 天活跃的记忆(窗口内创建,或旧记忆在窗口内被使用过)。</summary>
public sealed class MemRecentTool : ToolBase
{
    private readonly MemoryPlugin _plugin;

    public MemRecentTool(MemoryPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "mem_recent",
        "List this agent's memories active within the last N days — created inside the window OR used (mem_use) inside it — newest first. " +
        "The result is truncated from the oldest side to fit the remaining context budget.",
        """
        {
          "type": "object",
          "properties": {
            "days": { "type": "integer", "minimum": 1, "description": "Look-back window in days" }
          },
          "required": ["days"]
        }
        """,
        displayName: "最近记忆");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var days = (int)MemoryToolSupport.GetRequiredInt(argumentsJson, "days");
        if (days is < 1 or > 3650)
            throw new ArgumentException("days must be between 1 and 3650.");

        return Task.FromResult(MemoryToolSupport.QueryWithBudget(
            _plugin, () => _plugin.Store.Recent(days), "last " + days + " day(s)"));
    }
}

/// <summary>mem_range:指定时间区间内创建或最后使用过的记忆。</summary>
public sealed class MemRangeTool : ToolBase
{
    private readonly MemoryPlugin _plugin;

    public MemRangeTool(MemoryPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "mem_range",
        "List this agent's memories created or last used within [from, to], oldest first. " +
        "Timestamps are ISO 8601 (e.g. 2026-09-01 or 2026-09-01T14:30) or unix seconds; a date-only 'to' includes the whole day. " +
        "from > to is auto-swapped. The result is truncated from the oldest side to fit the remaining context budget.",
        """
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "Start timestamp (ISO 8601 or unix seconds)" },
            "to": { "type": "string", "description": "End timestamp (ISO 8601 or unix seconds); date-only means end of that day" }
          },
          "required": ["from", "to"]
        }
        """,
        displayName: "区间记忆");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var fromRaw = MemoryToolSupport.GetRequiredString(argumentsJson, "from");
        var toRaw = MemoryToolSupport.GetRequiredString(argumentsJson, "to");
        if (!MemoryToolSupport.TryParseTime(fromRaw, endOfDayIfDateOnly: false, out var from))
            throw new ArgumentException("'from' is not a valid timestamp (ISO 8601 or unix seconds).");
        if (!MemoryToolSupport.TryParseTime(toRaw, endOfDayIfDateOnly: true, out var to))
            throw new ArgumentException("'to' is not a valid timestamp (ISO 8601 or unix seconds).");
        if (from > to)
        {
            (from, to) = (to, from); // 模型写反了区间:自动交换而不是报错
        }

        return Task.FromResult(MemoryToolSupport.QueryWithBudget(
            _plugin, () => _plugin.Store.Range(from, to),
            "between " + MemoryToolSupport.Local(from) + " and " + MemoryToolSupport.Local(to),
            chronological: true));
    }
}

/// <summary>mem_use:使用一条记忆(使用数+1、刷新最后使用时间),并返回其内容。</summary>
public sealed class MemUseTool : ToolBase
{
    private readonly MemoryPlugin _plugin;

    public MemUseTool(MemoryPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "mem_use",
        "Mark one memory as used now (increments its use count and refreshes its last-used time, so it survives auto-forget) " +
        "and return its content. Call this when you actually apply a recalled memory to your reply.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The memory id (from mem_recent / mem_range / mem_search)" }
          },
          "required": ["id"]
        }
        """,
        displayName: "使用记忆");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var id = MemoryToolSupport.GetRequiredInt(argumentsJson, "id");
        _plugin.ForgetSweep();
        var entry = _plugin.Store.Use(id);
        if (entry is null)
            return Task.FromResult("Memory #" + id + " not found (it may have been auto-forgotten).");
        return Task.FromResult(MemoryToolSupport.FormatEntry(entry.Value) + " | marked used now.");
    }
}

/// <summary>mem_edit:更新一条记忆的内容;content 传空字符串则删除该条(遗忘)。</summary>
public sealed class MemEditTool : ToolBase
{
    private readonly MemoryPlugin _plugin;

    public MemEditTool(MemoryPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "mem_edit",
        "Update an existing memory's content, or forget it by passing an empty content string. " +
        "Use when a stored fact changed or was corrected (e.g. the user says they moved houses), or is no longer true. " +
        "Returns the updated entry, or a confirmation that the memory was forgotten.",
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "integer", "description": "The memory id (from mem_recent / mem_range / mem_search)" },
            "content": { "type": "string", "description": "New content (at most 2000 chars); pass an empty string to forget the memory" }
          },
          "required": ["id", "content"]
        }
        """,
        displayName: "修改记忆");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect; // 内部存储,非用户文件

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        long id;
        string content;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("id", out var idEl))
                throw new ArgumentException("id is required.");
            // content 必须显式给出:缺省不视为遗忘,避免参数缺失时误删记忆
            if (!doc.RootElement.TryGetProperty("content", out var cEl))
                throw new ArgumentException("content is required (pass an empty string to forget it).");
            id = idEl.GetInt64();
            content = (cEl.GetString() ?? "").Trim();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }

        if (content.Length > MemoryToolSupport.MaxContentChars)
            throw new ArgumentException($"content exceeds {MemoryToolSupport.MaxContentChars} characters; split it into several shorter memories.");

        _plugin.ForgetSweep();
        var (found, entry) = _plugin.Store.Edit(id, content);
        if (!found)
            return Task.FromResult("Memory #" + id + " not found (it may have been auto-forgotten).");
        if (entry is null)
            return Task.FromResult("Memory #" + id + " forgotten.");
        return Task.FromResult(MemoryToolSupport.FormatEntry(entry.Value) + " | updated.");
    }
}

/// <summary>mem_search:多关键词模糊搜索记忆内容(任一关键词命中)。</summary>
public sealed class MemSearchTool : ToolBase
{
    private readonly MemoryPlugin _plugin;

    public MemSearchTool(MemoryPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "mem_search",
        "Fuzzy-search this agent's memory contents for keywords (case-insensitive substring; a memory matches if it contains ANY of the given keywords). " +
        "When unsure which term was used, pass several alternative terms in one call. " +
        "Results are ranked by number of matched keywords, then recency, and truncated from the least relevant side to fit the remaining context budget.",
        """
        {
          "type": "object",
          "properties": {
            "keywords": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Keyword list (at most 8); a memory matches if it contains ANY keyword"
            }
          },
          "required": ["keywords"]
        }
        """,
        displayName: "搜索记忆");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> keywords;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("keywords", out var kw))
                throw new ArgumentException("keywords is required.");
            // 数组为标准格式;兼容模型退化为单个空格/逗号分隔字符串的旧格式
            keywords = kw.ValueKind switch
            {
                JsonValueKind.Array => kw.EnumerateArray()
                    .Select(k => k.ValueKind == JsonValueKind.String ? (k.GetString() ?? "").Trim() : k.ToString())
                    .Where(k => k.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MemoryToolSupport.MaxKeywords)
                    .ToList(),
                JsonValueKind.String => MemoryToolSupport.SplitKeywords(kw.GetString() ?? ""),
                _ => throw new ArgumentException("keywords must be an array of strings."),
            };
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }
        if (keywords.Count == 0)
            throw new ArgumentException("keywords must contain at least one term.");

        // OR 搜索的相关性:命中关键词越多越靠前,同数按最近活动;预算截断从最不相关的一侧丢
        return Task.FromResult(MemoryToolSupport.QueryWithBudget(
            _plugin, () => _plugin.Store.Search(keywords),
            "matching '" + string.Join(" ", keywords) + "' (any keyword)",
            recencyOrder: hits => hits
                .OrderByDescending(e => MemoryToolSupport.CountKeywordHits(e.Content, keywords))
                .ThenByDescending(e => Math.Max(e.CreatedAt, e.LastUsedAt))
                .ToList()));
    }
}
