namespace APet.UI.Services;

/// <summary>工具功能组:同一功能的一组工具共用一个总开关(设置页按组展示与开关)。</summary>
public sealed record ToolGroupInfo(string Name, string Description, IReadOnlyList<string> Tools);

/// <summary>
/// 内置工具目录:功能分组与中文说明。当前全部插件均为内置插件,分组静态声明;
/// 未收录的工具名归入"其他"组兜底,不会丢失开关能力。
/// </summary>
public static class ToolCatalog
{
    /// <summary>兜底组名:未归入任何分组的工具。</summary>
    public const string OtherGroupName = "其他";

    /// <summary>MCP 服务器工具名统一前缀:这类工具在「MCP」插件页按服务器/工具单独启停,不在工具启停页重复展示。</summary>
    public const string McpToolPrefix = "mcp_";

    /// <summary>由插件设置页单独管控的工具(不随工具启停页展示):B站直播插件的 mute/bili_live_events。</summary>
    private static readonly HashSet<string> s_pluginManagedTools =
        new(StringComparer.Ordinal) { "mute", "bili_live_events" };

    /// <summary>工具是否应在「工具」启停页展示(MCP 工具/B站直播插件工具在各自插件页管控,此处隐藏)。</summary>
    public static bool ShowOnToolPage(string toolName) =>
        !toolName.StartsWith(McpToolPrefix, StringComparison.Ordinal)
        && !s_pluginManagedTools.Contains(toolName);

    public static readonly IReadOnlyList<ToolGroupInfo> Groups =
    [
        new("基础",
            "与用户交互的基础能力:结构化提问(ask)在真正需要用户拍板时向用户发起(模糊需求、多种可行方案、破坏性操作)。",
            ["ask"]),
        new("文件读写",
            "读取、创建与修改工作目录中的文件;apply_patch 按补丁块精确改写已有文件。",
            ["read_file", "write_file", "edit_file", "apply_patch"]),
        new("终端命令",
            "在系统默认 Shell 中执行单条命令,返回输出与退出码(超时保护,默认 120 秒)。",
            ["bash"]),
        new("网页抓取",
            "抓取指定 URL 的网页内容并转为文本,供模型阅读(与 LLM 请求共用代理设定)。",
            ["web_fetch"]),
        new("视觉",
            "获取画面交支持视觉的模型查看:screenshot 截取主屏/指定屏幕,view_image 读取磁盘上的图片文件。",
            ["screenshot", "view_image"]),
        new("长期记忆",
            "跨会话共享的持久记忆:记录、近期/区间查询、关键词搜索、修正与标记使用。",
            ["mem_record", "mem_recent", "mem_range", "mem_use", "mem_edit", "mem_search"]),
        new("任务清单",
            "侧边栏展示的任务清单:规划多步任务并随进度更新状态(按会话隔离)。",
            ["todo_write", "todo_read"]),
        new("后台运行",
            "把耗时命令放到后台执行:随时查看增量输出、等待结束或按需终止进程(按会话隔离)。",
            ["bg_run", "bg_output", "bg_wait", "bg_kill", "bg_list"]),
        new("技能",
            "按需加载技能说明:skill 工具把所选技能的完整说明注入上下文(技能清单在技能页维护)。",
            ["skill"]),
        new("子代理",
            "把自包含子任务委派给预配置子代理:sub_run 发起、sub_wait 等待其最终答复、sub_tell 运行中传话(子代理清单在设置页维护)。",
            ["sub_run", "sub_wait", "sub_tell", "sub_list", "sub_kill"]),
    ];

    /// <summary>工具名 → 所属组的展示名(未收录的归"其他"组)。</summary>
    public static string GroupNameOf(string toolName) =>
        Groups.FirstOrDefault(g => g.Tools.Contains(toolName))?.Name ?? OtherGroupName;

    /// <summary>
    /// 动态工具的中文展示名兜底:动态工具(如 B站直播插件的 bili_live_events)不注册在 ToolRegistry 里,
    /// 仅在所属插件启用时存在,查不到 Definition,设置页展示时按此回退。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DynamicToolDisplayNames =
        new Dictionary<string, string>
        {
            ["bili_live_events"] = "获取直播间事件",
        };
}
