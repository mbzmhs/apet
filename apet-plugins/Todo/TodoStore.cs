using System.Text.Json;

namespace APet.Plugins.Todo;

/// <summary>
/// 任务清单存储:每个会话一份(会话隔离),JSON 持久化到 agents/&lt;agentId&gt;/todos/&lt;sessionId&gt;.json。
/// 线程安全;任意变更触发 Changed(可能在工具执行线程,UI 侧自行调度回 UI 线程)。
/// </summary>
public sealed class TodoStore : IDisposable
{
    /// <summary>合法状态值(小写下划线):待办 / 进行中 / 已完成。</summary>
    public const string StatusPending = "pending";
    public const string StatusInProgress = "in_progress";
    public const string StatusCompleted = "completed";

    private readonly object _gate = new();
    private readonly string _path;
    private List<TodoItem> _items = [];

    /// <summary>清单发生变化(整体替换/清空)后触发。</summary>
    public event Action? Changed;

    public TodoStore(string agentsRoot, string agentId, string sessionId)
    {
        var dir = Path.Combine(agentsRoot,
            string.Concat((agentId ?? "default").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')),
            "todos",
            string.Concat(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')));
        _path = Path.Combine(dir, "todo.json");
        Load();
    }

    /// <summary>当前清单快照(按 id 升序)。</summary>
    public IReadOnlyList<TodoItem> Snapshot()
    {
        lock (_gate)
            return _items.OrderBy(i => i.Id).Select(i => i.Clone()).ToList();
    }

    /// <summary>整体替换清单(空列表即清空);id 由存储重新分配,状态归一化为合法值。</summary>
    public void Replace(IEnumerable<TodoItem> items)
    {
        lock (_gate)
        {
            _items = items.Select(i => new TodoItem
            {
                Content = (i.Content ?? "").Trim(),
                Status = NormalizeStatus(i.Status),
            }).Where(i => i.Content.Length > 0).ToList();
            for (var i = 0; i < _items.Count; i++)
                _items[i].Id = i + 1;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>非法状态值一律按 pending 处理。</summary>
    private static string NormalizeStatus(string? status) => status switch
    {
        StatusInProgress => StatusInProgress,
        StatusCompleted => StatusCompleted,
        _ => StatusPending,
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var items = new List<TodoItem>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var content = el.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var status = el.TryGetProperty("status", out var s) ? s.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(content))
                        items.Add(new TodoItem { Content = content, Status = NormalizeStatus(status) });
                }
            }
            lock (_gate)
            {
                _items = items;
                for (var i = 0; i < _items.Count; i++)
                    _items[i].Id = i + 1;
            }
        }
        catch
        {
            // 损坏的清单文件视为不存在,不阻断启动
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(
                _items.Select(i => new { content = i.Content, status = i.Status }).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // 磁盘写入失败不抛出:工具结果照常返回,下次变更再尝试
        }
    }

    public void Dispose() { }
}

/// <summary>清单中的一条任务。</summary>
public sealed class TodoItem
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public string Status { get; set; } = TodoStore.StatusPending;

    public TodoItem Clone() => new() { Id = Id, Content = Content, Status = Status };
}
