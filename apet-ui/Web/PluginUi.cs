using System.IO;
using System.Collections.Concurrent;
using System.Reflection;
using APet.Framework.Plugin;

namespace APet.UI.Services;

/// <summary>
/// 插件 UI 资产:DLL 内嵌资源中以 "ui" 段为根的文件
/// (如 HelloPlugin.ui.index.html → index.html;ui/assets/app.js → HelloPlugin.ui.assets.app.js → assets/app.js),
/// 经 GET /api/agents/{agentId}/external-plugins/{name}/ui/{path} 伺服给同源 iframe。
/// 资产随 DLL 发布(自包含,更新即替换 DLL);清单按程序集缓存(进程内 DLL 不变)。
/// 约定:ui 目录的资源在 csproj 里用 <EmbeddedResource Include="ui\**\*" /> 嵌入。
/// </summary>
public static class PluginUi
{
    private static readonly ConcurrentDictionary<Assembly, (bool Has, List<(string Path, string Resource)> Files)> Cache = new();

    private static (bool Has, List<(string Path, string Resource)> Files) Resolve(Assembly assembly) =>
        Cache.GetOrAdd(assembly, a =>
        {
            var files = new List<(string, string)>();
            foreach (var name in a.GetManifestResourceNames())
            {
                var parts = name.Split('.');
                var idx = Array.IndexOf(parts, "ui");
                // 至少 ui.<name>.<ext> 三段;"ui." 段自身不足或 "ui" 在末尾都不是 UI 资产
                if (idx < 0 || parts.Length < idx + 3)
                    continue;
                var rest = parts[(idx + 1)..];
                // 约定:最后一个点 = 扩展名分隔;之前的点 = 目录分隔(根目录下带点的文件名不支持)
                var dir = rest.Length > 2 ? string.Join('/', rest[..^2]) : "";
                var file = rest[^2] + "." + rest[^1];
                files.Add((dir.Length > 0 ? dir + "/" + file : file, name));
            }
            return (files.Count > 0, files);
        });

    /// <summary>插件程序集是否携带 UI 资产(ui.* 内嵌资源)。</summary>
    public static bool HasUi(AgentPluginBase plugin) =>
        Resolve(plugin.GetType().Assembly).Has;

    /// <summary>插件是否声明某 UI 插槽(UiSlots 逗号分隔,忽略大小写)。</summary>
    public static bool HasSlot(AgentPluginBase plugin, string slot) =>
        plugin.UiSlots.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(slot, StringComparer.OrdinalIgnoreCase);

    /// <summary>按相对路径(assets/app.js 形式,空 = index.html)读 UI 资产字节;不存在返回 null。</summary>
    public static byte[]? TryRead(AgentPluginBase plugin, string? path)
    {
        if (string.IsNullOrEmpty(path))
            path = "index.html";
        var files = Resolve(plugin.GetType().Assembly).Files;
        foreach (var (p, r) in files)
        {
            if (!string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = plugin.GetType().Assembly.GetManifestResourceStream(r);
            if (stream is null)
                return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        return null;
    }
}
