using System.IO;

namespace APet.UI.Services;

/// <summary>
/// 运行期数据根目录(Windows 约定:用户数据 Roaming、机器本地缓存 Local,程序目录只留程序):
/// - 数据根(默认 %APPDATA%\APet):全局 setting.json 与 agents\(设定/会话/记忆/清单/图片/头像)——用户资产,重装保留;
/// - 缓存根(默认 %LOCALAPPDATA%\APet):logs、webview2 等可再生数据,以及会话临时工作区 workspaces。
/// 环境变量 APET_DATA_DIR / APET_CACHE_DIR 可整体覆盖(便携部署把两者指到部署目录即可)。
/// </summary>
internal static class AppPaths
{
    /// <summary>用户数据根:setting.json + agents\。</summary>
    public static string DataRoot { get; } = FromEnv("APET_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APet");

    /// <summary>机器本地根:logs\、webview2\(workspaces 由 Security.WorkspaceRoot 单独配置,默认同在此根下)。</summary>
    public static string CacheRoot { get; } = FromEnv("APET_CACHE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APet");

    /// <summary>全部 Agent 的档案目录(agents\{id}:setting.json + sessions/ + images/ + memory.db + todos/ + avatar)。</summary>
    public static string AgentsRoot => Path.Combine(DataRoot, "agents");

    /// <summary>运行日志目录(apet.log)。</summary>
    public static string LogsRoot => Path.Combine(CacheRoot, "logs");

    /// <summary>WebView2 用户数据目录(Chromium profile 缓存)。</summary>
    public static string WebView2Root => Path.Combine(CacheRoot, "webview2");

    private static string? FromEnv(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
