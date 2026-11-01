using APet.Framework.Logging;

namespace APet.Framework.Plugin;

/// <summary>
/// 插件注册对象:Enabled 开关的官方入口。SetEnabled 切换时自动调用插件的
/// Initialize(启用)或 Dispose(禁用),并把状态同步回插件自身的 Enabled 属性。
/// 启用时若插件重写了异步初始化(InitializeAsync),其任务经 <see cref="EnsureInitialized"/> 可被 await
/// (宿主构造路径后台执行,Web 启用端点同步等待)。
/// </summary>
public sealed class PluginRegistration : IDisposable
{
    private readonly PluginRegistry _registry;
    private Task? _initTask;

    internal PluginRegistration(IAgentPlugin plugin, PluginRegistry registry)
    {
        Plugin = plugin;
        _registry = registry;
    }

    public IAgentPlugin Plugin { get; }

    public string Name => Plugin.Name;

    public bool Enabled { get; private set; }

    /// <summary>切换启用状态,自动驱动 Initialize / Dispose。
    /// 实例的 Plugin.Enabled 始终同步写入(注册为禁用时插件实例不应保持默认 true,
    /// 否则直接读取实例 Enabled 的调用方——如桌宠并发闸门、mute 工具——会误判)。</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
        {
            Plugin.Enabled = enabled;
            return;
        }
        Enabled = enabled;
        Plugin.Enabled = enabled; // 实例标志同步写入:完整路径此前漏写,启用后实例 Enabled 仍是
                                  // Register(…, false) 短路写入的 false——桌宠闸门/mute 等直读实例标志全部误判
        try
        {
            if (enabled)
            {
                Plugin.Initialize();
                // 异步预热在后台进行;失败只记日志(钩子须容忍未就绪状态)
                if (Plugin is AgentPluginBase basePlugin)
                {
                    _initTask = RunAsyncInit(Name, basePlugin);
                }
                else
                {
                    _initTask = null;
                }
            }
            else
            {
                _initTask = null; // 放弃尚未完成的初始化
                Plugin.Dispose();
            }
        }
        catch
        {
            // 初始化失败回滚开关:不回滚则注册中心按"已启用"分发(插件清单/工具重新出现),
            // 而插件实例实际未初始化——状态与事实相反
            Enabled = !enabled;
            Plugin.Enabled = !enabled;
            throw;
        }
        finally
        {
            _registry.InvalidateEnabledCache();
        }
    }

    private static async Task RunAsyncInit(string name, AgentPluginBase plugin)
    {
        try
        {
            await plugin.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Global.Error($"[Plugin] {name} InitializeAsync failed: {ex}");
        }
    }

    /// <summary>等待异步初始化完成(未重写异步初始化或已禁用时立即完成)。Web 启用端点用于保证"保存即生效"。</summary>
    public Task EnsureInitialized() => _initTask ?? Task.CompletedTask;

    /// <summary>禁用并移出插件集合。</summary>
    public void Dispose()
    {
        SetEnabled(false);
        _registry.Remove(this);
    }
}
