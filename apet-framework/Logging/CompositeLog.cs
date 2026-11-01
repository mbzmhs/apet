namespace APet.Framework.Logging;

/// <summary>组合多个日志输出(例如同时写控制台与文件)。</summary>
public sealed class CompositeLog : ILog
{
    private readonly ILog[] _logs;

    public CompositeLog(params ILog[] logs)
    {
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
    }

    public void Debug(string message)
    {
        foreach (var log in _logs) log.Debug(message);
    }

    public void Info(string message)
    {
        foreach (var log in _logs) log.Info(message);
    }

    public void Error(string message, Exception? exception = null)
    {
        foreach (var log in _logs) log.Error(message, exception);
    }
}
