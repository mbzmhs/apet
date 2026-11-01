using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using APet.UI.ViewModels;

namespace APet.UI.Web;

/// <summary>
/// 单会话事件总线:多个订阅者(浏览器标签 / 内嵌 WebView2)各持独立有界通道,发布即扇出。
/// 慢订阅丢旧帧(流式增量可丢,前端以快照兜底),发布方永不阻塞。
/// </summary>
public sealed class SessionEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    /// <summary>最后一个订阅者退订时回调(登记簿据此回收键,防键无限增长)。</summary>
    internal Action? OnEmpty { private get; set; }

    /// <summary>订阅:返回该订阅者专属通道的读取端与退订句柄(连接断开即退订)。</summary>
    public (ChannelReader<string> Reader, IDisposable Subscription) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (channel.Reader, new Unsub(this, id));
    }

    /// <summary>发布一帧(SSE 已序列化文本)给全部订阅者;通道线程安全,可在任意线程调用。</summary>
    public void Publish(string frame)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(frame);
    }

    private sealed class Unsub(SessionEventBus bus, Guid id) : IDisposable
    {
        public void Dispose()
        {
            if (bus._subscribers.TryRemove(id, out _) && bus._subscribers.IsEmpty)
                bus.OnEmpty?.Invoke();
        }
    }
}

/// <summary>
/// Web 事件中枢:全部会话的事件总线注册表 + 工具授权登记簿。引擎(MainViewModel,UI 线程)
/// 与 SSE 端点(Kestrel 线程)经此交互;发布不依赖 Dispatcher,鉴权卡片的注册/解析都在 UI 线程。
/// </summary>
public static class WebEventHub
{
    private static readonly ConcurrentDictionary<string, SessionEventBus> Buses = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, PendingAuth> PendingAuths = new(StringComparer.Ordinal);
    private static int _authSeq;

    private static string Key(string agentId, string sessionId) => agentId + "|" + sessionId;

    public static SessionEventBus Bus(string agentId, string sessionId)
    {
        var key = Key(agentId, sessionId);
        return Buses.GetOrAdd(key, _ =>
        {
            var bus = new SessionEventBus();
            bus.OnEmpty = () => ReleaseIfEmpty(key, bus);
            return bus;
        });
    }

    /// <summary>最后一个订阅者退订:登记簿仅在键仍映射到该实例时原子移除
    /// (新订阅者已换新实例则不动)。会话删除/页面关闭后键不再累积。</summary>
    private static void ReleaseIfEmpty(string key, SessionEventBus bus) =>
        ((ICollection<KeyValuePair<string, SessionEventBus>>)Buses).Remove(KeyValuePair.Create(key, bus));

    /// <summary>发布事件:帧 = {"type":"…","data":{…}}(camelCase;每帧只序列化一次)。</summary>
    public static void Publish(string agentId, string sessionId, string type, object? data = null)
    {
        var frame = JsonSerializer.Serialize(new EventFrame(type, data), Json.Web);
        Bus(agentId, sessionId).Publish(frame);
    }

    /// <summary>Agent 级事件总线(会话列表状态等跨会话推送):sessionId 以 "*" 占位,复用同一注册表。</summary>
    public static SessionEventBus AgentBus(string agentId) => Bus(agentId, "*");

    /// <summary>Agent 级事件发布(sessions_changed 等;帧格式与会话流一致)。</summary>
    public static void PublishAgent(string agentId, string type, object? data = null)
    {
        var frame = JsonSerializer.Serialize(new EventFrame(type, data), Json.Web);
        AgentBus(agentId).Publish(frame);
    }

    // ───────── 工具授权登记(卡片由 UiCallbacks 创建;Web 经 REST 给出结论) ─────────

    /// <summary>登记一张待决鉴权卡片并广播 auth_request;返回 REST 用的 authId(UI 线程调用)。</summary>
    public static string RegisterAuth(string agentId, string sessionId, AuthRequestViewModel card)
    {
        var id = "auth_" + Interlocked.Increment(ref _authSeq);
        card.WebAuthId = id; // 随快照下发:刷新后浏览器仍可对历史卡片给出结论
        PendingAuths[id] = new PendingAuth(agentId, sessionId, card);
        Publish(agentId, sessionId, "auth_request", new
        {
            authId = id,
            detail = card.Detail,
            trustDirs = card.TrustDirs,
        });
        return id;
    }

    /// <summary>按登记 Id 给出结论:trust = 信任目录并放行,否则按 allow。返回是否命中登记(UI 线程调用)。</summary>
    public static bool ResolveAuth(string authId, bool allow, bool trust)
    {
        if (!PendingAuths.TryRemove(authId, out var entry))
            return false;
        if (trust)
            entry.Card.Trust();
        else
            entry.Card.Resolve(allow);
        Publish(entry.AgentId, entry.SessionId, "auth_resolved", new { authId, granted = trust || allow });
        return true;
    }

    /// <summary>运行中断/收尾时回收被中断的鉴权卡:移出登记簿并广播关闭帧。
    /// 不调它则每张被中断的卡永久留在 PendingAuths(登记容易注销难)。</summary>
    public static void CancelAuth(string? authId)
    {
        if (authId is null || !PendingAuths.TryRemove(authId, out var entry))
            return;
        Publish(entry.AgentId, entry.SessionId, "auth_resolved", new { authId, granted = false });
    }

    private sealed record PendingAuth(string AgentId, string SessionId, AuthRequestViewModel Card);

    private sealed record EventFrame(string Type, object? Data);

    // ───────── ask 工具询问登记(工具经宿主委托挂起在 TCS;Web 经 REST 应答) ─────────

    private static readonly ConcurrentDictionary<string, PendingAsk> PendingAsks = new(StringComparer.Ordinal);
    private static int _askSeq;

    /// <summary>登记一次待决询问并广播 ask_request;返回 REST 用的 askId(任意线程可调)。</summary>
    public static string RegisterAsk(string agentId, string sessionId,
        IReadOnlyList<APet.Framework.Tools.BuiltIn.AskQuestion> questions, TaskCompletionSource<string?> tcs)
    {
        var id = "ask_" + Interlocked.Increment(ref _askSeq);
        PendingAsks[id] = new PendingAsk(agentId, sessionId, questions, tcs);
        Publish(agentId, sessionId, "ask_request", new { askId = id, questions });
        return id;
    }

    /// <summary>应答询问:answersJson = 用户答案(回传给模型),null = 跳过。返回是否命中登记。</summary>
    public static bool ResolveAsk(string askId, string? answersJson)
    {
        if (!PendingAsks.TryRemove(askId, out var entry))
            return false;
        entry.Tcs.TrySetResult(answersJson);
        Publish(entry.AgentId, entry.SessionId, "ask_resolved", new { askId });
        return true;
    }

    /// <summary>取消待决询问(运行中断等):按用户未作答处理。</summary>
    public static void CancelAsk(string askId) => ResolveAsk(askId, null);

    /// <summary>该会话当前待决询问清单(页面刷新后重建询问面板)。</summary>
    public static List<object> PendingAsksFor(string agentId, string sessionId) =>
        PendingAsks
            .Where(kv => string.Equals(kv.Value.AgentId, agentId, StringComparison.Ordinal)
                         && string.Equals(kv.Value.SessionId, sessionId, StringComparison.Ordinal))
            .Select(kv => (object)new { askId = kv.Key, questions = kv.Value.Questions })
            .ToList();

    private sealed record PendingAsk(
        string AgentId,
        string SessionId,
        IReadOnlyList<APet.Framework.Tools.BuiltIn.AskQuestion> Questions,
        TaskCompletionSource<string?> Tcs);
}

internal static class Json
{
    /// <summary>Web 序列化统一用 camelCase(与前端约定一致)。</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
