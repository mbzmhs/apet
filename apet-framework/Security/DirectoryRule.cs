namespace APet.Framework.Security;

/// <summary>
/// 白名单目录:优先级最高,位于白名单内的路径任何操作都放行,
/// 不再走工作目录/其他目录的级别判定。
/// </summary>
public sealed class DirectoryRule
{
    /// <summary>白名单目录(绝对路径)。</summary>
    public required string Path { get; init; }

    /// <summary>
    /// 限定的工具权限类别(ToolPermission.GetKey,如 read/edit/terminal)。
    /// null/空 = 对所有工具放行;否则仅该类别工具匹配本条规则。
    /// </summary>
    public string? Tool { get; init; }
}
