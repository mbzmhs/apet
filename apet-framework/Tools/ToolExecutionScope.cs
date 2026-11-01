namespace APet.Framework.Tools;

/// <summary>
/// 一次工具执行的环境(AsyncLocal,跨 await 隔离):会话工作目录等。
/// ToolRegistry 在效果分析与执行外层设置;内置工具据此解析相对路径、
/// 终端据此设置子进程工作目录,使"会话工作目录"对工具生效。
/// </summary>
public static class ToolExecutionScope
{
    private static readonly AsyncLocal<string?> WorkingDirectoryValue = new();
    private static readonly AsyncLocal<string?> SessionIdValue = new();
    private static readonly AsyncLocal<IReadOnlySet<string>?> AllowedToolsValue = new();
    private static readonly AsyncLocal<Func<int>?> ContextUsageValue = new();
    private static readonly AsyncLocal<Action<string, string>?> OutputValue = new();

    /// <summary>当前执行环境的工作目录;未设置时为 null(工具回退到自身配置或进程目录)。</summary>
    public static string? WorkingDirectory => WorkingDirectoryValue.Value;

    /// <summary>当前执行环境的会话标识(会话级安全策略传入);供按会话隔离状态的插件工具(TODO/后台任务)分桶。</summary>
    public static string? SessionId => SessionIdValue.Value;

    /// <summary>本轮可调用的工具白名单(会话级安全策略传入);null = 不限制。ToolRegistry 在调用时据此统一拦截。</summary>
    public static IReadOnlySet<string>? AllowedTools => AllowedToolsValue.Value;

    /// <summary>
    /// 当前工作消息的实时 Token 用量提供器(Agent 运行期间由 ToolRegistry 注入,求值时计算最新值);
    /// 供需要"上下文预算"的工具(如记忆插件按剩余上下文截断返回)使用;非 Agent 运行时为 null。
    /// </summary>
    public static Func<int>? ContextUsage => ContextUsageValue.Value;

    /// <summary>
    /// 工具执行中的实时输出出口(参数 = 流名 stdout/stderr 与文本增量);Agent 运行期间由 ToolRegistry 注入,
    /// 供长时间运行的工具(如运行终端)边执行边把输出推给宿主显示;宿主未接线时为 null(输出只在结束时回传)。
    /// </summary>
    public static Action<string, string>? Output => OutputValue.Value;

    internal static IDisposable Enter(string? workingDirectory, string? sessionId,
        IReadOnlySet<string>? allowedTools, Func<int>? contextUsage = null, Action<string, string>? output = null) =>
        new Scope(workingDirectory, sessionId, allowedTools, contextUsage, output);

    private sealed class Scope : IDisposable
    {
        private readonly string? _previousDir;
        private readonly string? _previousSession;
        private readonly IReadOnlySet<string>? _previousAllowed;
        private readonly Func<int>? _previousUsage;
        private readonly Action<string, string>? _previousOutput;

        public Scope(string? workingDirectory, string? sessionId, IReadOnlySet<string>? allowedTools,
            Func<int>? contextUsage, Action<string, string>? output)
        {
            _previousDir = WorkingDirectoryValue.Value;
            WorkingDirectoryValue.Value = workingDirectory;
            _previousSession = SessionIdValue.Value;
            SessionIdValue.Value = sessionId;
            _previousAllowed = AllowedToolsValue.Value;
            AllowedToolsValue.Value = allowedTools;
            _previousUsage = ContextUsageValue.Value;
            ContextUsageValue.Value = contextUsage;
            _previousOutput = OutputValue.Value;
            OutputValue.Value = output;
        }

        public void Dispose()
        {
            WorkingDirectoryValue.Value = _previousDir;
            SessionIdValue.Value = _previousSession;
            AllowedToolsValue.Value = _previousAllowed;
            ContextUsageValue.Value = _previousUsage;
            OutputValue.Value = _previousOutput;
        }
    }
}
