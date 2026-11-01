using System.Text;

namespace APet.Framework.Logging;

/// <summary>控制台日志输出,按等级着色,线程安全,支持最低等级过滤。</summary>
public sealed class ConsoleLog : ILog
{
    private static readonly object s_lock = new();

    private readonly string? _source;
    private readonly LogLevel _minLevel;

    public ConsoleLog(string? source = null, LogLevel minLevel = LogLevel.Debug)
    {
        _source = source;
        _minLevel = minLevel;
    }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _minLevel)
            return;

        var builder = new StringBuilder();
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        builder.Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']');
        if (!string.IsNullOrEmpty(_source))
            builder.Append(" [").Append(_source).Append(']');
        builder.Append(' ').Append(message);
        if (exception is not null)
            builder.AppendLine().Append(exception);

        var line = builder.ToString();
        lock (s_lock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Info => previous,
                LogLevel.Error => ConsoleColor.Red,
                _ => previous,
            };
            Console.WriteLine(line);
            Console.ForegroundColor = previous;
        }
    }
}
