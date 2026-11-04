namespace APet.Framework.Security;

/// <summary>路径归属判定。</summary>
public static class PathScope
{
    /// <summary>判断 path 是否位于 root 之内(含等于 root)。非法路径返回 false。
    /// 字符串在内还不够:路径上任何一级是 junction/symlink 且最终目标落在 root 外时,
    /// 穿透读写实际落在 root 外(如 mklink 建链后经文件工具穿链写盘),同样判定为不在内。</summary>
    public static bool Contains(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var rootFull = FullWithoutTrailingSeparator(root);
            var pathFull = Path.GetFullPath(path);
            if (!IsWithin(pathFull, rootFull))
                return false;
            return !EscapesViaReparsePoint(pathFull, rootFull);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>字符串包含判定(Windows 忽略大小写):path 等于 root 或位于 root 目录下。</summary>
    private static bool IsWithin(string pathFull, string rootFull) =>
        string.Equals(pathFull, rootFull, Comparison)
        || pathFull.StartsWith(rootFull + Path.DirectorySeparatorChar, Comparison);

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string FullWithoutTrailingSeparator(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// path 或其已存在祖先是否经重解析点(junction/symlink)最终落到 root 外。
    /// 不存在的层级跳过(尚未创建,无从穿透);root 自身不检查(用户指定即信任,根是链接属正常配置);
    /// 探测失败按逃逸处理(宁可多问,不静默放行)。性能:每次工具调用最多探一层目录链,可忽略。
    /// </summary>
    public static bool EscapesViaReparsePoint(string pathFull, string rootFull)
    {
        try
        {
            var current = pathFull;
            while (!string.IsNullOrEmpty(current))
            {
                if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
                    break; // 到根为止:根以下才是受管辖范围
                FileSystemInfo? final = Directory.Exists(current)
                    ? Directory.ResolveLinkTarget(current, returnFinalTarget: true)
                    : File.Exists(current)
                        ? File.ResolveLinkTarget(current, returnFinalTarget: true)
                        : null;
                if (final is not null)
                {
                    var finalFull = FullWithoutTrailingSeparator(final.FullName);
                    if (!IsWithin(finalFull, rootFull))
                        return true; // 链接终点在根外:穿透读写逃逸
                }
                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent.Length >= current.Length)
                    break;
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return true;
        }
        return false;
    }
}

/// <summary>鉴权器抽象,可替换为自定义实现(例如接入外部权限系统)。</summary>
public interface ISecurityChecker
{
    /// <summary>
    /// 按策略判定工具调用效果。返回 Allowed 表示放行;
    /// 返回非 Allowed 不代表最终拒绝,而是"策略未放行",由调用方转交宿主回调决定。
    /// </summary>
    AuthorizationResult Check(AgentSecurityPolicy policy, ToolEffect effect, string? toolName = null);
}

/// <summary>默认鉴权器:白名单 &gt; 工作目录 &gt; 其他目录,按三级授权级别放行。</summary>
public sealed class SecurityManager : ISecurityChecker
{
    public static SecurityManager Instance { get; } = new();

    public AuthorizationResult Check(AgentSecurityPolicy policy, ToolEffect effect, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(effect);

        var toolKey = toolName is null ? null : ToolPermission.GetKey(toolName);

        // 终端命令不做逐路径 ACL:三套仿真解析器的失败模式必须是"弹窗"而非"放行",
        // 故裁决只认三类可靠结论(只读正清单/灾难清单/写入范围),其余一律交宿主回调。
        // 档位语义:ReadOnly=仅只读;ReadWrite=追加"写工作目录";FullAccess=全放行;Ask=全部确认。
        if (toolKey == "terminal")
            return CheckTerminal(policy, effect);

        // 规则匹配:路径在目录内,且规则未限定工具(或与当前工具的权限类别一致)
        bool InWhitelist(string path) => InWhitelistedDirectory(policy, path, toolKey);

        if (effect.Access == FileAccessKind.None)
            return AuthorizationResult.Allow;

        // 单一档位语义:完全访问 = 文件工具全放行(含目录外与效果未知);全部询问 = 文件工具全弹
        if (policy.WorkingDirectoryAccess == DirectoryAccess.FullAccess)
            return AuthorizationResult.Allow;
        if (policy.WorkingDirectoryAccess == DirectoryAccess.Ask)
            return AuthorizationResult.Deny("当前权限档为「全部询问」,所有工具调用需逐次确认");

        // 白名单次之:所有已知路径都落在白名单内时,任何操作(含效果未知)都放行
        if (effect.Paths.Count > 0 && effect.Paths.All(InWhitelist))
            return AuthorizationResult.Allow;

        // 效果未知无法按读写种类判定:未命中白名单/完全访问档时一律弹卡(fail-closed)
        if (effect.Access == FileAccessKind.Unknown)
            return AuthorizationResult.Deny("the effect of this call could not be determined (unknown) and requires host approval");

        foreach (var path in effect.Paths)
        {
            if (InWhitelist(path))
                continue;

            var granted = ResolveAccess(policy, path);
            var satisfied = effect.Access switch
            {
                // 只读/读写/完全访问都包含读;全部询问(Ask)不自动放行任何操作
                FileAccessKind.Read => granted is DirectoryAccess.ReadOnly or DirectoryAccess.ReadWrite or DirectoryAccess.FullAccess,
                FileAccessKind.Write => granted is DirectoryAccess.ReadWrite or DirectoryAccess.FullAccess,
                // 未知效果仅完全访问放行(实际上已在上方转交回调)
                _ => granted == DirectoryAccess.FullAccess,
            };
            if (!satisfied)
                return AuthorizationResult.Deny(
                    $"{effect.Access} access to '{path}' is not granted by the policy (directory access: {granted}); host approval is required");
        }

        return AuthorizationResult.Allow;
    }

    /// <summary>路径归属解析:工作目录内按档位;目录外固定按只读判(写/未知效果弹卡),白名单已在上方先行放行。</summary>
    private static DirectoryAccess ResolveAccess(AgentSecurityPolicy policy, string path) =>
        !string.IsNullOrEmpty(policy.WorkingDirectory) && PathScope.Contains(policy.WorkingDirectory, path)
            ? policy.WorkingDirectoryAccess
            : DirectoryAccess.ReadOnly;

    /// <summary>终端命令裁决:档位取工作目录权限,四档语义——
    /// 只读=仅放行只读正清单,写/未知弹卡;读写=除灾难清单外静默执行;
    /// 完全访问=全放行;全部询问=全弹。白名单保留为只读档的逃生门。</summary>
    private static AuthorizationResult CheckTerminal(AgentSecurityPolicy policy, ToolEffect effect)
    {
        var tier = policy.WorkingDirectoryAccess;

        // 灾难清单命中:任何非完全访问档位都逐次确认,白名单也不豁免
        if (effect.Dangerous && tier != DirectoryAccess.FullAccess)
            return AuthorizationResult.Deny("危险命令(递归删除/格式化/强推等),需逐次确认");

        if (tier == DirectoryAccess.FullAccess)
            return AuthorizationResult.Allow;

        if (tier == DirectoryAccess.Ask)
            return AuthorizationResult.Deny("当前权限档为「全部询问」,所有终端命令需逐次确认");

        // 逃生门:确认卡片"信任此目录"写入的白名单,命令涉及的全部路径都落在信任范围内时放行
        if (effect.Paths.Count > 0 && effect.Paths.All(p => InWhitelistedDirectory(policy, p, "terminal")))
            return AuthorizationResult.Allow;

        // 只读档:只读正清单(静态可靠)放行,写入与未知效果一律弹卡
        if (tier == DirectoryAccess.ReadOnly)
            return effect.Access is FileAccessKind.Read or FileAccessKind.None
                ? AuthorizationResult.Allow
                : AuthorizationResult.Deny("只读档下终端仅放行只读命令,写入与未知效果需确认");

        // 读写档:灾难清单已在最前拦截,其余命令(含未知效果、目录外写)静默执行
        return AuthorizationResult.Allow;
    }

    /// <summary>白名单匹配:路径在规则目录内,且规则未限定工具(或与当前工具权限类别一致)。</summary>
    private static bool InWhitelistedDirectory(AgentSecurityPolicy policy, string path, string? toolKey) =>
        policy.WhitelistedDirectories.Any(r =>
            PathScope.Contains(r.Path, path)
            && (string.IsNullOrEmpty(r.Tool) || (toolKey is not null && r.Tool == toolKey)));
}
