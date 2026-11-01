using System.IO;
using System.Text;

namespace APet.Framework.Logging;

/// <summary>
/// 滚动文本文件日志(追加写,超 5MB 截断):异常堆栈等诊断信息落盘,便于事后定位"偶发"问题。
/// 写失败静默忽略(磁盘/占用问题不能反过来影响运行)。
/// </summary>
public sealed class FileLog : ILog
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    public FileLog(string path) => _path = path;

    public void Debug(string message) => Write("DBG", message);

    public void Info(string message) => Write("INF", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERR", exception is null ? message : message + Environment.NewLine + exception);

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.WriteAllText(_path, string.Empty); // 超限截断(保留最新)
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写失败不影响运行
        }
    }
}
