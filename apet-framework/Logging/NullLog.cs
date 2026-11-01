namespace APet.Framework.Logging;

/// <summary>空实现,不输出任何日志。</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Debug(string message) { }

    public void Info(string message) { }

    public void Error(string message, Exception? exception = null) { }
}
