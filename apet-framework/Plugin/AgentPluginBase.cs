using System.Collections.Concurrent;
using System.Reflection;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;
using AgentClass = APet.Framework.Agent.Agent;

namespace APet.Framework.Plugin;

/// <summary>
/// 暴露给插件的 Agent 宿主配置只读视图:宿主把标注 [PluginVisible] 的配置属性按键名开放。
/// 活读——设置页保存对同一配置实例原地改值,插件下一次读自动是新值;复杂值经 GetJson 取 JSON 文本。
/// 键名是对外契约(宿主侧显式命名,见插件指南);插件编译期只依赖本接口,不引用宿主类型。
/// </summary>
public interface IAgentConfigView
{
    /// <summary>按键名活读当前值;未知键返回 null。</summary>
    object? Get(string key);

    /// <summary>按键名取当前值的 JSON 文本(复杂配置对象用;无该键或值为 null 返回 null)。</summary>
    string? GetJson(string key);

    /// <summary>当前宿主开放的全部键名。</summary>
    IReadOnlyCollection<string> Keys { get; }
}

/// <summary>
/// 插件基类:提供 Enabled 开关、全部消息链钩子的虚实现、基于
/// [PluginSetting] 注解的设置能力(GetSettings/GetSetting/SetSetting/TrySetSetting),
/// 以及可选的"插件即工具"能力(IsTool,见 ToolDefinition/ExecuteToolAsync)。
/// 子类必须实现 Name、Initialize、Dispose,钩子按需重写,设置项用属性 + 注解声明。
/// </summary>
public abstract class AgentPluginBase : IAgentPlugin
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<SettingProperty>> s_settingsCache = new();

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    public abstract string Name { get; }

    /// <summary>启用时调用(Enabled false→true)。宿主可能以 UI 线程上下文进入本方法:
    /// 同步方法内不得做阻塞式网络/IO;需要异步预热请重写 <see cref="InitializeAsync"/>。</summary>
    public abstract void Initialize();

    /// <summary>禁用时调用(Enabled true→false)。异步清理请重写 <see cref="DisposeAsync"/>。</summary>
    public abstract void Dispose();

    /// <summary>
    /// 异步初始化阶段:框架在同步 Initialize 之后 await 本方法。宿主构造路径在后台执行(不阻塞启动),
    /// Web 启用端点会 await 到完成。用于建连接/加载模型等无法同步完成的预热;默认无操作。
    /// 快速反复启停时,禁用会放弃尚未完成的初始化——钩子须容忍"尚未就绪"状态。
    /// </summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// 异步销毁阶段:宿主在运行时销毁/应用退出时,于同步 Dispose 之后 await 本方法。
    /// 用于无法同步完成的清理(等待在途请求结束等);默认无操作。
    /// </summary>
    public virtual Task DisposeAsync() => Task.CompletedTask;

    // ───────── 元数据(设置页插件卡片与外部插件列表展示;LoadOrder 决定注册顺序) ─────────

    /// <summary>
    /// 本插件实例所属的 Agent id(宿主注册后注入,未注入为空)。
    /// 插件在会话之外发布 Agent 级事件时,用它作 agentId:
    /// <c>PluginEvents.PublishAgent(AgentId, "…", …)</c>。
    /// </summary>
    public string AgentId { get; set; } = "";

    /// <summary>
    /// 只读视图:暴露给插件的 Agent 宿主配置(宿主注册时注入,未注入为 null)。
    /// 按键名活读当前值(设置页保存后自动生效);复杂值用 <see cref="IAgentConfigView.GetJson"/>。
    /// 可用键名由宿主决定,见 docs/external-plugin-guide.md「宿主配置视图」节。
    /// </summary>
    public IAgentConfigView? AgentConfigView { get; set; }

    /// <summary>插件版本(展示用,可空)。</summary>
    public virtual string? Version => null;

    /// <summary>插件作者(展示用,可空)。</summary>
    public virtual string? Author => null;

    /// <summary>一句话描述(设置页卡片展示,可空)。</summary>
    public virtual string? Description => null;

    /// <summary>
    /// 注册顺序:数字小者先注册(默认 0,同数按 Name 升序)。
    /// 需要依赖其他插件先完成初始化时,用本属性声明顺序。
    /// </summary>
    public virtual int LoadOrder => 0;

    /// <summary>每次 LLM 请求前的异步钩子(含工具循环中的每一轮),默认无操作;供运行中检查类插件(如上下文压缩)重写。</summary>
    public virtual Task OnBeforeRequestAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>一次 LLM 请求(轮次)结束后的异步钩子(工具轮含全部工具结果;末轮在结果写回后),默认无操作;
    /// 供"请求结束后重算阈值"类检查(如上下文压缩)重写。
    /// 注意:重写时参数必须同样声明为可选(= default),否则接口分派不绑定本方法(静默失效)。</summary>
    public virtual Task OnAfterTurnAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>一轮运行结束(成功/中断/出错三态都会触发;OnAfterTurnAsync 只在成功路径触发)。重写时不得阻塞,异常仅记日志。</summary>
    public virtual Task OnRunFinishedAsync(AgentClass agent, AgentSession session, RunOutcome outcome) =>
        Task.CompletedTask;

    /// <summary>会话生命周期事件(Created/Removed);重写时不得阻塞,异常仅记日志。</summary>
    public virtual void OnSessionEvent(AgentClass agent, AgentSession session, SessionEventKind kind) { }

    // ───────── 前端数据面:宿主 REST 端点 /plugins/{name}/data 与侧边栏数据区派发到此处 ─────────

    /// <summary>
    /// 本插件是否提供前端数据(侧边栏为 ProvidesData=true 的插件渲染数据区;默认 false)。
    /// 数据区按侧边栏既有节奏刷新(数秒内),即时变更可另经 <see cref="PluginEvents"/> 推送事件。
    /// </summary>
    public virtual bool ProvidesData => false;

    /// <summary>
    /// UI 插槽声明(逗号分隔;空 = 无)。当前支持:
    /// "settings"——设置页卡片的"设置"按钮直接打开本插件 DLL 内嵌的 ui.* 资产页(无 UI 资产时回退自动生成的通用表单);
    /// "sidebar"——侧边栏为本插件单开一个自定义页签(替代 JSON 数据区)。
    /// 插槽页面经宿主同源 iframe 渲染,插件页引用 /apet-bridge.js 获得数据面/事件/宿主 DOM 能力。
    /// 详见 docs/external-plugin-guide.md「自定义 UI」章。
    /// </summary>
    public virtual string UiSlots => "";

    /// <summary>
    /// GET 数据面:返回任意可 JSON 序列化对象(或 null=无数据)。宿主以 UI 线程派发,不得阻塞。
    /// sessionId 为请求方提供的会话 id(可为 null=Agent 级)。
    /// </summary>
    public virtual object? GetPluginData(string? sessionId) => null;

    /// <summary>
    /// POST 数据面:body 为请求体原始 JSON 文本(可为 null);返回任意可 JSON 序列化对象(或 null=无结果)。
    /// 宿主以 UI 线程派发,不得阻塞。
    /// </summary>
    public virtual object? HandlePluginData(string? sessionId, string? body) => null;

    /// <summary>插件注册的聊天框快捷指令(宿主以 $ 前缀提供补全与执行);默认无。</summary>
    public virtual IReadOnlyList<PluginChatCommand> GetChatCommands() => [];

    /// <inheritdoc />
    public virtual string? GetSystemPromptSegment(AgentSession session) => null;

    public virtual Task OnBeforeRunAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual void OnBeforeRequest(AgentClass agent, AgentSession session, List<ChatMessage> messages) { }

    /// <inheritdoc />
    public virtual void OnStreamDelta(AgentClass agent, AgentSession session, StreamDeltaContext delta) { }

    /// <inheritdoc />
    public virtual void OnAssistantMessage(AgentClass agent, AgentSession session, ChatMessage assistantMessage) { }

    // ───────── 扩展工具能力:插件可作为一个工具参与 LLM 请求与执行 ─────────

    /// <summary>
    /// 是否把本插件作为工具暴露给 LLM(默认否)。
    /// 置为 true 后:工具定义随每次请求发送,模型调用时经 ToolRegistry 的动态工具管线执行
    /// (与普通工具一致地通过安全鉴权与宿主回调);禁用插件即同时下线该工具。
    /// </summary>
    public virtual bool IsTool => false;

    /// <summary>插件作为工具的定义。IsTool 为 true 时必须重写。</summary>
    public virtual ToolDefinition ToolDefinition =>
        throw new NotSupportedException($"Plugin {Name} does not expose a tool. Set IsTool = true and override {nameof(ToolDefinition)}.");

    /// <summary>插件作为工具的执行入口(模型真的调用该工具时走这里)。IsTool 为 true 时必须重写。</summary>
    public virtual Task<string> ExecuteToolAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Plugin {Name} does not expose a tool. Set IsTool = true and override {nameof(ExecuteToolAsync)}.");

    /// <summary>
    /// 富工具执行入口:返回可携带图片附件的 <see cref="ToolResult"/>。
    /// 默认包装字符串版 <see cref="ExecuteToolAsync"/>(旧插件不受影响);
    /// 需要返回多模态结果(截图/看图类工具)时重写本方法而非字符串版。
    /// </summary>
    public virtual async Task<ToolResult> ExecuteToolResultAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        ToolResult.FromText(await ExecuteToolAsync(argumentsJson, cancellationToken).ConfigureAwait(false));

    /// <summary>插件作为工具的影响解析(安全鉴权用),默认 Unknown(交由宿主回调决定)。</summary>
    public virtual ToolEffect AnalyzeToolEffect(string argumentsJson) => ToolEffect.UnknownEffect;

    // ───────── 设置能力:属性 + [PluginSetting] 注解,父类自带实现 ─────────

    /// <summary>枚举本插件声明的全部设置项(名称、类型、限制、当前值),供宿主渲染设置界面。</summary>
    public IReadOnlyList<PluginSettingDescriptor> GetSettings() =>
        ResolveSettings(GetType()).Select(s => new PluginSettingDescriptor
        {
            Name = s.Name,
            Description = s.Attribute.Description,
            ValueType = PluginSettingBinder.DescribeType(s.Property.PropertyType),
            Min = s.Attribute.Min,
            Max = s.Attribute.Max,
            MinLength = s.Attribute.MinLength,
            MaxLength = s.Attribute.MaxLength,
            Options = ResolveSettingOptions(s),
            Label = s.Attribute.Label,
            Group = s.Attribute.Group,
            UiHint = s.Attribute.UiHint,
            CurrentValue = s.Property.GetValue(this),
        }).ToList();

    /// <summary>读取设置当前值。名称不区分大小写;未知名称抛出 PluginSettingException。</summary>
    public object? GetSetting(string name)
    {
        var setting = FindSetting(name)
            ?? throw new PluginSettingException($"Unknown setting '{name}' on plugin {Name}.");
        return setting.Property.GetValue(this);
    }

    /// <summary>写入设置,类型自动转换并按注解校验;失败抛出 PluginSettingException,原值保持不变。</summary>
    public void SetSetting(string name, object? value)
    {
        if (!TrySetSetting(name, value, out var error))
            throw new PluginSettingException(error!);
    }

    /// <summary>写入设置的容错版本。失败时返回 false 并通过 error 给出原因,原值保持不变。</summary>
    public bool TrySetSetting(string name, object? value, out string? error)
    {
        var setting = FindSetting(name);
        if (setting is null)
        {
            error = $"Unknown setting '{name}' on plugin {Name}.";
            return false;
        }

        try
        {
            setting.Property.SetValue(
                this,
                PluginSettingBinder.ConvertValue(setting.Name, setting.Property.PropertyType, value, setting.Attribute));
            error = null;
            OnSettingChanged(setting.Name);
            return true;
        }
        catch (PluginSettingException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 批量写入设置:先对全部条目做类型转换与校验,全部通过后一次性应用;
    /// 任一条目有问题则抛出 PluginSettingException(聚合全部错误),所有值保持不变。
    /// </summary>
    public void SetSettings(IEnumerable<KeyValuePair<string, object?>> settings)
    {
        if (!TrySetSettings(settings, out var error))
            throw new PluginSettingException(error!);
    }

    /// <summary>批量写入设置的容错版本,错误信息聚合全部失败条目。</summary>
    public bool TrySetSettings(IEnumerable<KeyValuePair<string, object?>> settings, out string? error)
    {
        var all = ResolveSettings(GetType());
        var errors = new List<string>();
        var pending = new List<(SettingProperty Setting, object? Converted)>();

        foreach (var (name, value) in settings)
        {
            var setting = all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (setting is null)
            {
                errors.Add($"Unknown setting '{name}' on plugin {Name}.");
                continue;
            }

            try
            {
                var converted = PluginSettingBinder.ConvertValue(
                    setting.Name, setting.Property.PropertyType, value, setting.Attribute);
                pending.Add((setting, converted));
            }
            catch (PluginSettingException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            return false;
        }

        var changed = new List<SettingProperty>();
        foreach (var (setting, converted) in pending)
        {
            if (!Equals(setting.Property.GetValue(this), converted))
            {
                setting.Property.SetValue(this, converted);
                changed.Add(setting);
            }
        }
        // 仅对真正变化的设置触发回调:批量保存里未改动的项不应触发重连/重载等副作用
        foreach (var setting in changed)
            OnSettingChanged(setting.Name);
        error = null;
        return true;
    }

    /// <summary>设置成功写入后的回调(含批量设置中每个成功条目),用于触发重连/重建等副作用。</summary>
    protected virtual void OnSettingChanged(string name) { }

    private static string[] ResolveSettingOptions(SettingProperty s) =>
        s.Attribute.Options is { Length: > 0 } options ? options
        : s.Property.PropertyType.IsEnum ? Enum.GetNames(s.Property.PropertyType)
        : Array.Empty<string>();

    private SettingProperty? FindSetting(string name) =>
        ResolveSettings(GetType()).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SettingProperty> ResolveSettings(Type pluginType) =>
        s_settingsCache.GetOrAdd(pluginType, t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<PluginSettingAttribute>() })
            .Where(x => x.Attribute is not null && x.Property.CanRead && x.Property.CanWrite)
            .Select(x => new SettingProperty(
                x.Property,
                x.Attribute!.Name is { Length: > 0 } name ? name : x.Property.Name,
                x.Attribute!))
            .ToList());

    private sealed record SettingProperty(PropertyInfo Property, string Name, PluginSettingAttribute Attribute);
}
