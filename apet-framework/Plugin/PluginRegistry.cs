namespace APet.Framework.Plugin;

/// <summary>插件注册中心:注册/移除插件,按 Enabled 过滤出参与运行的插件。
/// UI 线程启停插件、Kestrel/agent 线程枚举并发访问:内部一律加锁,对外只暴露快照。</summary>
public sealed class PluginRegistry
{
    private readonly object _gate = new();
    private readonly List<PluginRegistration> _plugins = new();
    private IReadOnlyList<PluginRegistration>? _enabledCache;

    /// <summary>
    /// 注册插件。enabled 为 true 时立即调用其 Initialize;
    /// 同名插件不允许重复注册。
    /// </summary>
    public PluginRegistration Register(IAgentPlugin plugin, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        PluginRegistration registration;
        lock (_gate)
        {
            if (_plugins.Any(p => p.Name == plugin.Name))
                throw new InvalidOperationException($"Plugin {plugin.Name} is already registered.");

            registration = new PluginRegistration(plugin, this);
            _plugins.Add(registration);
            InvalidateEnabledCache();
        }
        // SetEnabled(会触发 Initialize/事件)必须挪出锁外:插件初始化可能反查注册表,锁内做会自锁
        registration.SetEnabled(enabled);
        return registration;
    }

    /// <summary>移除插件(禁用其 Initialize/Dispose 生命周期后移出)。</summary>
    public bool Remove(PluginRegistration registration)
    {
        bool removed;
        lock (_gate)
        {
            removed = _plugins.Remove(registration);
            if (removed)
                InvalidateEnabledCache();
        }
        return removed;
    }

    public PluginRegistration? Find(string name)
    {
        lock (_gate)
            return _plugins.FirstOrDefault(p => p.Name == name);
    }

    /// <summary>全部已注册插件(按注册顺序,含禁用)快照;宿主枚举插件工具(如 skill)用。</summary>
    public IReadOnlyList<PluginRegistration> All
    {
        get { lock (_gate) return _plugins.ToList(); }
    }

    /// <summary>当前启用的插件(按注册顺序)快照。缓存结果,开关变化时自动失效。</summary>
    public IReadOnlyList<PluginRegistration> EnabledPlugins =>
        _enabledCache ??= BuildEnabledSnapshot();

    private IReadOnlyList<PluginRegistration> BuildEnabledSnapshot()
    {
        lock (_gate)
            return _plugins.Where(p => p.Enabled).ToList();
    }

    internal void InvalidateEnabledCache() => _enabledCache = null;
}
