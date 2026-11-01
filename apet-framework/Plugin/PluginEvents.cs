using APet.Framework.Session;

namespace APet.Framework.Plugin;

/// <summary>
/// 插件 → 前端事件通道:宿主启动时注入 sink(转发到会话/Agent 级 SSE 事件总线),
/// 未注入时全部发布为空操作。任何线程可调用;发布不阻塞(总线自带缓冲与丢弃策略)。
/// 事件帧与会话流其余事件同形:{"type":"…","data":{…}}。
/// 约定:插件数据(ProvidesData)变更时发布 type "plugin_data_changed",
/// 前端侧边栏数据区会随既有刷新节奏重新拉取。
/// </summary>
public static class PluginEvents
{
    /// <summary>会话级 sink 签名:(sessionId, type, data) => 是否已发布。宿主负责 sessionId → agentId 解析。</summary>
    public static Func<string, string, object?, bool>? SessionSink { get; set; }

    /// <summary>Agent 级 sink 签名:(agentId, type, data) => 是否已发布(跨会话事件,如插件自身状态)。</summary>
    public static Func<string, string, object?, bool>? AgentSink { get; set; }

    /// <summary>向当前会话的事件流发布一条事件(无订阅者时返回 false)。</summary>
    public static bool Publish(AgentSession session, string type, object? data = null) =>
        SessionSink?.Invoke(session.Id, type, data) ?? false;

    /// <summary>向 Agent 级事件流发布一条事件(无订阅者时返回 false)。</summary>
    public static bool PublishAgent(string agentId, string type, object? data = null) =>
        AgentSink?.Invoke(agentId, type, data) ?? false;
}
