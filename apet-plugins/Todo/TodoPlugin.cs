using System.Collections.Concurrent;
using APet.Framework.Plugin;
using APet.Framework.Tools;

namespace APet.Plugins.Todo;

/// <summary>
/// 任务清单插件:由宿主注册 todo_write / todo_read 两个工具,Agent 用它维护多步任务的
/// 计划与进度。清单按会话隔离(工具执行时经 ToolExecutionScope.SessionId 分桶),
/// 持久化在 agents/&lt;agentId&gt;/todos/&lt;sessionId&gt;.json;宿主侧边栏随选中会话切换展示。
/// </summary>
public sealed class TodoPlugin : AgentPluginBase
{
    private readonly string _agentId;
    private readonly string _agentsRoot;
    private readonly ConcurrentDictionary<string, TodoStore> _stores = new();

    public TodoPlugin(string agentId, string agentsRoot) => (_agentId, _agentsRoot) = (agentId, agentsRoot);

    public override string Name => "todo";
    public override void Initialize() { }
    public override void Dispose()
    {
        foreach (var store in _stores.Values)
            store.Dispose();
        _stores.Clear();
    }

    /// <summary>取指定会话的清单存储(工具执行与侧边栏共用);会话未知时归入 default 桶。</summary>
    public TodoStore StoreFor(string? sessionId)
    {
        var key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId!;
        return _stores.GetOrAdd(key, id => new TodoStore(_agentsRoot, _agentId, id));
    }
}
