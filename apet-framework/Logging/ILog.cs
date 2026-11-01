namespace APet.Framework.Logging;

/// <summary>统一日志接口。框架内部全部通过该接口输出日志,宿主注入实现即可接管。</summary>
public interface ILog
{
    /// <summary>调试信息:协议细节、消息内容等,仅在排查问题时开启。</summary>
    void Debug(string message);

    /// <summary>一般信息:请求开始/结束、工具调用等关键节点。</summary>
    void Info(string message);

    /// <summary>错误信息,可附带异常对象。</summary>
    void Error(string message, Exception? exception = null);
}
