using System.IO;
using System.Threading;
using System.Windows;
using APet.Framework.Logging;
using APet.Framework.Tools.ShellIntent;
using APet.PsParser;
using APet.UI.Services;

namespace APet.UI;

/// <summary>
/// 应用入口:不再用 StartupUri 自动建窗,改由 AgentManager 接管——
/// 启动时初始化所有已启用 Agent(默认不弹窗),常驻系统托盘;退出仅由托盘"退出"触发。
/// 单实例保护:各 Agent 设定是读入内存、保存时整份写回各自文件的,
/// 两个实例并存时后保存的会用旧状态覆盖新状态(表现为设置保存后"丢"),故第二个实例直接退出。
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private AgentManager? _agentManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 文件日志:框架与 LLM 客户端的运行记录、异常堆栈落盘(logs/apet.log,超 5MB 截断),
        // 偶发问题(如压缩报错)事后可凭堆栈定位
        Log.Global = new CompositeLog(new ConsoleLog(),
            new FileLog(Path.Combine(Services.AppPaths.LogsRoot, "apet.log")));
        // PowerShell 命令的权限解析用官方 AST 解析器(精确仿真意图);未注入时框架走保守回退
        IntentAnalyze.PowerShellAnalyzer = new PowerShellIntentAnalyzer();
        _singleInstanceMutex = new Mutex(true, "APet.UI.SingleInstance", out var isNew);
        if (!isNew)
        {
            Views.MessageDialogWindow.Show(null, "APet", "APet 已在运行,请通过系统托盘打开。");
            Shutdown();
            return;
        }
        _agentManager = new AgentManager();
        // v2:先起本地 Web 服务再初始化 Agent(WebShellWindow 与浏览器都导航到它)
        Web.WebServer.Start(_agentManager);
        Log.Global.Info($"Web UI: {Web.WebServer.Instance.BaseUrl}");
        _agentManager.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _agentManager?.Dispose();
        try
        {
            Web.WebServer.Instance?.Stop();
        }
        catch
        {
            // 退出路径:尽力而为
        }
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
