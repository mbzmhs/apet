using System.Text.Json;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;

namespace APet.Plugins.Todo;

/// <summary>todo_write:整体替换任务清单(增删改状态都通过重写完成)。</summary>
public sealed class TodoWriteTool : ToolBase
{
    private readonly TodoPlugin _plugin;

    public TodoWriteTool(TodoPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "todo_write",
        "Replace the shared todo list shown in the user's sidebar; pass the FULL list every time (items you omit are deleted). " +
        "Use it to plan and track non-trivial work: before starting a task with multiple steps (roughly 3 or more), write a short list of concrete, actionable items; skip it for a single quick action. " +
        "Each item is a short actionable step with a status (pending / in_progress / completed). " +
        "Keep exactly ONE item in_progress at a time: set it before starting that step, mark it completed immediately after finishing it, then move the next item to in_progress. " +
        "Rewrite the whole list on every update; drop items that became irrelevant and add newly discovered steps.",
        """
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "description": "The complete new todo list, in display order",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "description": "Short actionable description (at most 200 characters)" },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"] }
                },
                "required": ["content", "status"]
              }
            }
          },
          "required": ["items"]
        }
        """,
        displayName: "更新任务清单");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect; // 内部清单,非用户文件

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        List<TodoItem> items;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("items (array) is required.");
            if (arr.GetArrayLength() > MaxItems)
                throw new ArgumentException($"items exceeds {MaxItems} entries; keep the list focused.");
            items = [];
            foreach (var el in arr.EnumerateArray())
            {
                var content = (el.TryGetProperty("content", out var c) ? c.GetString() : null) ?? "";
                var status = (el.TryGetProperty("status", out var s) ? s.GetString() : null) ?? TodoStore.StatusPending;
                if (content.Length > MaxContentChars)
                    throw new ArgumentException($"item content exceeds {MaxContentChars} characters; keep each item short.");
                items.Add(new TodoItem { Content = content, Status = status });
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Invalid arguments JSON: " + ex.Message);
        }

        var store = _plugin.StoreFor(ToolExecutionScope.SessionId);
        store.Replace(items);
        return Task.FromResult(Summary(store.Snapshot()));
    }

    /// <summary>给模型的确认文本:各状态条数 + 当前清单(省得模型再调 todo_read)。</summary>
    private static string Summary(IReadOnlyList<TodoItem> items)
    {
        if (items.Count == 0)
            return "Todo list cleared.";
        var counts = items.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count());
        int pending = counts.GetValueOrDefault(TodoStore.StatusPending),
            active = counts.GetValueOrDefault(TodoStore.StatusInProgress),
            done = counts.GetValueOrDefault(TodoStore.StatusCompleted);
        var sb = new System.Text.StringBuilder($"Todo list updated: {pending} pending, {active} in progress, {done} completed.\n");
        foreach (var item in items)
            sb.Append($"#{item.Id} [{item.Status}] {item.Content}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>清单条数上限(过长的清单失去"计划"意义,也省上下文)。</summary>
    public const int MaxItems = 30;

    /// <summary>单条内容长度上限。</summary>
    public const int MaxContentChars = 200;
}

/// <summary>todo_read:查看当前任务清单。</summary>
public sealed class TodoReadTool : ToolBase
{
    private readonly TodoPlugin _plugin;

    public TodoReadTool(TodoPlugin plugin) => _plugin = plugin;

    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        "todo_read",
        "Read the current shared todo list (shown in the user's sidebar), oldest item first.",
        """{ "type": "object", "properties": {} }""",
        displayName: "查看任务清单");

    public override ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        var items = _plugin.StoreFor(ToolExecutionScope.SessionId).Snapshot();
        if (items.Count == 0)
            return Task.FromResult("The todo list is empty.");
        var sb = new System.Text.StringBuilder();
        foreach (var item in items)
            sb.Append($"#{item.Id} [{item.Status}] {item.Content}\n");
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
