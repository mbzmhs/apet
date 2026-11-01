namespace APet.Framework.Security;

/// <summary>
/// 单个 Agent 的安全鉴权策略(每个 Agent 各持有一份,互不影响)。
/// 策略只做放行判定,自身没有"直接拒绝"的黑名单动作:
/// 未满足鉴权的调用(如只读目录写入、效果未知的调用)一律交由宿主回调处理,回调默认拒绝。
/// 目录判定优先级:白名单 &gt; 工作目录 &gt; 其他目录默认级别。
/// </summary>
public sealed class AgentSecurityPolicy
{
    /// <summary>用户/主体标识,用于审计日志。</summary>
    public string UserId { get; set; } = "default";

    /// <summary>会话标识(会话级策略传入):随工具执行环境注入,供按会话隔离状态的插件工具(TODO/后台任务)分桶。</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// 本轮可调用的工具白名单(null = 不限制)。由 ToolRegistry 在调用时统一拦截——
    /// 不改动请求中的工具定义(保证提示词缓存命中),供特定来源的对话轮(如 B站直播触发)收窄可用工具。
    /// </summary>
    public IReadOnlySet<string>? AllowedTools { get; set; }

    /// <summary>Agent 的工作目录。为空时所有路径都落入"其他目录"的默认级别。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>工作目录内的授权级别,默认读写。</summary>
    public DirectoryAccess WorkingDirectoryAccess { get; set; } = DirectoryAccess.ReadWrite;

    /// <summary>其他目录(且不在白名单内)的授权级别,默认只读。</summary>
    public DirectoryAccess OtherDirectoriesAccess { get; set; } = DirectoryAccess.ReadOnly;

    /// <summary>白名单目录,优先级最高,内含路径任何操作都放行。可随意添加。</summary>
    public List<DirectoryRule> WhitelistedDirectories { get; } = new();

    /// <summary>添加白名单目录(tool 为 null 时对所有工具放行,否则仅限该权限类别)。</summary>
    public void AddWhitelistedDirectory(string path, string? tool = null) =>
        WhitelistedDirectories.Add(new DirectoryRule { Path = path, Tool = tool });
}
