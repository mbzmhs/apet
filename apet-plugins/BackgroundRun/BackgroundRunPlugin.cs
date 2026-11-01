using System.Collections.Concurrent;
using APet.Framework.Plugin;
using APet.Framework.Tools;

namespace APet.Plugins.BackgroundRun;

/// <summary>
/// 后台运行插件:由宿主注册 bg_run / bg_output / bg_wait / bg_kill / bg_list 五个工具,
/// Agent 可把耗时命令(开发服务器、构建、下载等)放到后台跑,随时查输出、终止。
/// 任务按会话隔离(工具执行时经 ToolExecutionScope.SessionId 分桶,内存态,
/// 进程不随应用重启存活);宿主侧边栏随选中会话切换展示。
/// </summary>
public sealed class BackgroundRunPlugin : AgentPluginBase
{
    private readonly ConcurrentDictionary<string, BackgroundTaskManager> _managers = new();

    /// <summary>
    /// 后台命令使用的终端(bash / cmd / powershell,由宿主按 Agent 设定推送);
    /// 为空表示沿用历史默认(CMD)。与「运行终端」工具同一选择,统一生效。
    /// </summary>
    public string? Shell { get; set; }

    public override string Name => "background";
    public override void Initialize() { }
    public override void Dispose()
    {
        foreach (var manager in _managers.Values)
            manager.Dispose();
        _managers.Clear();
    }

    /// <summary>取指定会话的任务管理器(工具执行与侧边栏共用);会话未知时归入 default 桶。</summary>
    public BackgroundTaskManager ManagerFor(string? sessionId)
    {
        var key = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId!;
        return _managers.GetOrAdd(key, _ => new BackgroundTaskManager());
    }
}
