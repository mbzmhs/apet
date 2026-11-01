namespace APet.Framework.Tools.BuiltIn;

/// <summary>文件类工具的公共配置。</summary>
public sealed class FileToolOptions
{
    /// <summary>
    /// 允许访问的根目录。设置后:相对路径基于它解析,且绝对路径必须位于其下。
    /// 为空时不限制(不推荐对不可信 LLM 开放)。
    /// </summary>
    public string? RootDirectory { get; set; }

    /// <summary>读取内容最大字符数,超出截断。</summary>
    public int MaxChars { get; set; } = 100_000;

    /// <summary>读取文件使用的编码名称,默认 utf-8(可用 gbk 等)。</summary>
    public string DefaultEncoding { get; set; } = "utf-8";
}
