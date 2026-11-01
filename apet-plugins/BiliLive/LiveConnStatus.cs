namespace APet.Plugins.BiliLive;

/// <summary>
/// 连接状态机(宿主侧边栏圆点与状态行显示用):
/// Idle → Resolving → Connecting → Authenticating → Connected;任意一步失败 → Retrying(退避后回 Resolving)。
/// 未开播且未开 connectWhenNotLive 时停在 WaitingLive 定时复查。
/// </summary>
public enum LiveConnState
{
    /// <summary>未启用/未配置房间号/连接引擎未启动。</summary>
    Idle,

    /// <summary>解析房间(get_info)/补登录身份/取弹幕 token 与线路中。</summary>
    Resolving,

    /// <summary>WS 握手中(逐条线路尝试)。</summary>
    Connecting,

    /// <summary>WS 已建立(101),认证帧已发等待应答中。</summary>
    Authenticating,

    /// <summary>认证通过,接收弹幕中。</summary>
    Connected,

    /// <summary>本轮失败,退避等待下次重连。</summary>
    Retrying,

    /// <summary>未开播且未开 connectWhenNotLive,60s 后复查。</summary>
    WaitingLive,
}

/// <summary>连接状态快照(不可变;写侧在锁内整体替换,读侧直接持有,无需再加锁)。</summary>
public sealed record LiveConnStatus(
    LiveConnState State,
    string Host,           // 最近尝试/已连的线路主机名(空 = 未尝试任何线路)
    int HostIndex,         // 最近尝试的线路序号(1-based)
    int HostCount,         // 本轮可用线路总数
    int Round,             // 重连轮数(从 1 起;认证成功后清零)
    int InternalRoomId,    // 解析出的内部房间号(0 = 未解析)
    bool RoomLive,         // 主播当前是否开播
    string Error,          // 最近一次失败原因(空 = 无;连接成功后清空)
    DateTime ChangedAt,    // 本状态进入时刻(本地)
    DateTime? NextRetryAt) // Retrying:下次重连时刻(其余状态 null)
{
    /// <summary>状态的小写稳定标识(前端/DTO 用,与枚举名解耦)。</summary>
    public string StateKey => State switch
    {
        LiveConnState.Resolving => "resolving",
        LiveConnState.Connecting => "connecting",
        LiveConnState.Authenticating => "authenticating",
        LiveConnState.Connected => "connected",
        LiveConnState.Retrying => "retrying",
        LiveConnState.WaitingLive => "waiting_live",
        _ => "idle",
    };
}
