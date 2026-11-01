namespace APet.Framework.Security;

/// <summary>工具调用对文件系统的影响方式。</summary>
public enum FileAccessKind
{
    /// <summary>不影响文件系统。</summary>
    None,

    /// <summary>只读。</summary>
    Read,

    /// <summary>写入/修改。</summary>
    Write,

    /// <summary>无法解析(按未知处理,交由宿主回调决定)。</summary>
    Unknown,
}

/// <summary>目录授权级别。策略只负责放行,不满足时一律交由宿主回调处理。</summary>
public enum DirectoryAccess
{
    /// <summary>只读:允许 Read。</summary>
    ReadOnly,

    /// <summary>读写:允许 Read、Write。</summary>
    ReadWrite,

    /// <summary>完全访问:任何操作都放行(含效果未知的调用)。</summary>
    FullAccess,

    /// <summary>全部询问:任何操作(含读取)都不自动放行,一律转交宿主确认。</summary>
    Ask,
}
