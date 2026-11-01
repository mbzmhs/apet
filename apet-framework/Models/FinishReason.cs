namespace APet.Framework.Models;

/// <summary>模型停止输出的原因。</summary>
public enum FinishReason
{
    /// <summary>正常结束,输出完整回复。</summary>
    Stop,

    /// <summary>模型请求调用工具。</summary>
    ToolCalls,

    Length,

    ContentFilter,

    /// <summary>无法识别的结束原因。</summary>
    Other,
}
