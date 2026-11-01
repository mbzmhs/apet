namespace APet.Framework.Logging;

/// <summary>
/// 框架级默认日志。组件未显式指定 ILog 时统一使用 Global,
/// 宿主在应用启动时替换 Global 即可接管整个框架的日志输出。
/// </summary>
public static class Log
{
    public static ILog Global { get; set; } = new ConsoleLog();
}
