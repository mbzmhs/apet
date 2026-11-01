using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using APet.Framework.Logging;
using APet.Framework.Plugin;

namespace APet.UI.Services;

/// <summary>
/// 外部插件加载器:扫描 {部署目录}\plugins\*.dll,发现实现 IAgentPlugin 的非抽象公开类型
/// (须带无参构造)。每个程序集独立 AssemblyLoadContext:插件自带依赖走 .deps.json/同目录探测,
/// APet.* 一律回落宿主共享版本保证类型贯通。
/// 加载发生在进程启动后首次取用时,单 DLL 失败仅记日志不影响宿主;已成功项进程内缓存;
/// **失败项每次 Discover 重试、目录中新出现的 DLL 也会补载**(新 Agent 创建/重启用会再次调用,
/// 修好或补上 DLL 后无需重启);已载入的 DLL 不卸载,覆盖更新后仍需重启生效。
/// </summary>
public static class ExternalPluginLoader
{
    private static readonly object _gate = new();
    private static readonly List<Type> _types = new();
    private static readonly HashSet<string> _handledDlls = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _failedDlls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>发现的外部插件类型(须继承 AgentPluginBase;成功项缓存,失败项与新增项每轮重试)。</summary>
    public static IReadOnlyList<Type> Discover()
    {
        lock (_gate)
        {
            var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
            var dlls = Directory.Exists(pluginsDir)
                ? Directory.GetFiles(pluginsDir, "*.dll")
                : Array.Empty<string>();
            var current = new HashSet<string>(dlls, StringComparer.OrdinalIgnoreCase);

            // 已记失败但被移走的 DLL 不再重试
            _failedDlls.RemoveWhere(failed => !current.Contains(failed));

            // 全部现存 DLL 都已成功处理且无失败项 → 零开销返回
            if (_failedDlls.Count == 0 && dlls.All(d => _handledDlls.Contains(d)))
                return _types.ToArray();

            var added = 0;
            foreach (var dll in dlls)
            {
                var name = Path.GetFileName(dll);
                if (IsHostAssembly(dll) || _handledDlls.Contains(dll))
                    continue;
                try
                {
                    var found = LoadPluginTypes(dll);
                    _types.AddRange(found);
                    _handledDlls.Add(dll);
                    _failedDlls.Remove(dll);
                    added += found.Count;
                }
                catch (Exception ex)
                {
                    _failedDlls.Add(dll);
                    Log.Global.Error($"[ExternalPlugins] failed to load {name}: {ex}");
                }
            }

            if (added > 0 || _failedDlls.Count > 0)
                Log.Global.Info($"[ExternalPlugins] discovered {_types.Count} plugin type(s) in plugins\\ ({added} new this pass, {_failedDlls.Count} failing)");
            return _types.ToArray();
        }
    }

    private static bool IsHostAssembly(string dll) =>
        Path.GetFileName(dll).StartsWith("APet.", StringComparison.OrdinalIgnoreCase);

    private static List<Type> LoadPluginTypes(string dll)
    {
        // 不可收集上下文:不卸载(覆盖更新后重启生效),无需 Dispose
        var context = new PluginLoadContext(dll);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            if (types.Length == 0)
                throw new InvalidOperationException("no loadable types: " + (ex.LoaderExceptions[0]?.Message ?? "unknown"), ex);
        }
        return types
            .Where(t => !t.IsAbstract && t.IsPublic && !t.IsInterface
                && typeof(AgentPluginBase).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null)
            .ToList();
    }
}

/// <summary>插件独立加载上下文:不卸载(更新插件后重启生效)。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // APet.* 回落宿主共享版本(类型贯通);其余先查插件自带依赖,最后回落默认解析
        if (assemblyName.Name?.StartsWith("APet.", StringComparison.OrdinalIgnoreCase) == true)
            return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return string.IsNullOrEmpty(path) ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return string.IsNullOrEmpty(path) ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>外部插件设置值的 JSON ↔ 通用值转换(宿主灌值与 Web 回写共用)。</summary>
public static class ExternalPluginValues
{
    /// <summary>JSON 值 → TrySetSettings 可接受的通用值(字符串/数字/布尔/原始 JSON 文本)。</summary>
    public static object? FromJson(JsonElement? element)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Null)
            return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => (object?)e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            _ => e.GetRawText(), // 数组/对象:交由绑定器按 JSON 文本解析(List<T> 等复合设置)
        };
    }

    /// <summary>描述符当前值 → 持久化袋 JSON(枚举以名称字符串承载,与 UI 显示一致)。</summary>
    public static JsonElement? ToJson(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(Normalize(value));

    /// <summary>枚举装箱值 → 枚举名字符串;其余原样。</summary>
    public static object? Normalize(object? value) => value is Enum e ? e.ToString() : value;
}
