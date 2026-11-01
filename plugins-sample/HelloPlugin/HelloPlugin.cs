using APet.Framework.Plugin;
using APet.Framework.Agent;
using APet.Framework.Session;

namespace HelloPlugin;

/// <summary>
/// 外部插件示例:演示 [PluginSetting] 的常用控件(文本/滑条/下拉/取色器/多行/列表)、
/// $hello 聊天指令、运行收尾三态钩子(OnRunFinishedAsync)、会话生命周期事件、
/// 元数据(Version/Author/Description)与前端数据面(侧边栏"插件"页签 + REST 数据面)。
/// 设置由设置页「内置插件 → 外部插件」按注解自动生成表单,保存经框架批量校验后持久化。
/// 详见 docs/external-plugin-guide.md。
/// </summary>
public sealed class HelloPlugin : AgentPluginBase
{
    public override string Name => "Hello";

    // ───────── 元数据:设置页卡片展示 ─────────

    public override string? Version => "1.0.0";
    public override string? Author => "APet";
    public override string? Description => "外部插件示例:设置、指令、运行收尾、数据面与自定义 UI";

    // 声明自定义 UI 插槽:设置页"自定义界面" + 侧边栏单开页签(内容 = ui/index.html)
    public override string UiSlots => "settings,sidebar";

    // ───────── 设置项:设置页自动渲染 ─────────

    [PluginSetting("greeting", Label = "问候语", Description = "打招呼使用的问候语前缀")]
    public string Greeting { get; set; } = "你好";

    [PluginSetting("volume", Label = "音量演示", Description = "0-100 的数字滑条", Min = 0, Max = 100, UiHint = "slider")]
    public int Volume { get; set; } = 50;

    [PluginSetting("style", Label = "风格", Description = "下拉选择:值必须在选项内", Options = ["normal", "formal", "funny"])]
    public string Style { get; set; } = "normal";

    [PluginSetting("themeColor", Label = "主题色", Description = "#RRGGBB 取色器", UiHint = "color")]
    public string ThemeColor { get; set; } = "#3B82F6";

    [PluginSetting("note", Label = "备注", Description = "多行文本", UiHint = "textarea")]
    public string Note { get; set; } = "";

    [PluginSetting("tags", Label = "标签列表", Description = "字符串数组,以 JSON 数组编辑")]
    public List<string> Tags { get; set; } = [];

    // 运行收尾统计:OnRunFinishedAsync 累加,数据面暴露给前端
    private int _completed;
    private int _cancelled;
    private int _failed;

    // Initialize/Dispose 为抽象成员,必须实现;无资源可不做任何事
    // 需要异步预热(建连接/加载模型)时再重写 InitializeAsync()
    public override void Initialize() { }
    public override void Dispose() { }

    // ───────── $ 聊天指令:输入 $hello / $stats 执行 ─────────

    public override IReadOnlyList<PluginChatCommand> GetChatCommands() =>
    [
        new PluginChatCommand(
            "hello",
            "按当前设置打一声招呼(外部插件示例)",
            TakesArgument: false,
            Execute: (_, _) =>
                $"{Greeting}!风格 = {Style},音量 = {Volume},颜色 = {ThemeColor},"
                + $"标签 = [{string.Join("、", Tags)}],备注 = {Note}"),
        new PluginChatCommand(
            "stats",
            "本插件统计的运行结局(成功/中断/失败)",
            TakesArgument: false,
            Execute: (_, _) =>
                $"成功 {_completed} 次 · 中断 {_cancelled} 次 · 失败 {_failed} 次"),
    ];

    // ───────── 运行收尾:成功/中断/出错三态都会送达(OnAfterTurnAsync 只在成功路径触发) ─────────

    public override Task OnRunFinishedAsync(Agent agent, AgentSession session, RunOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case RunOutcomeKind.Completed: _completed++; break;
            case RunOutcomeKind.Cancelled: _cancelled++; break;
            case RunOutcomeKind.Failed: _failed++; break;
        }
        // 数据面变更通知(可选):前端侧边栏随既有刷新节奏重拉 GetPluginData
        PluginEvents.Publish(session, "plugin_data_changed");
        return Task.CompletedTask;
    }

    // ───────── 会话生命周期:Created(新建/分叉/还原/移出归档)/ Removed(删除/归档) ─────────

    public override void OnSessionEvent(Agent agent, AgentSession session, SessionEventKind kind)
    {
        // 例:按会话记账、预热缓存;不得阻塞,异常仅记日志
    }

    // ───────── 前端数据面:侧边栏"插件"页签 + GET/POST /plugins/Hello/data?sessionId=… ─────────

    public override bool ProvidesData => true;

    public override object? GetPluginData(string? sessionId) =>
        new
        {
            completed = _completed, cancelled = _cancelled, failed = _failed,
            // 宿主配置视图:读取宿主 AgentConfig 上 [PluginVisible] 标注的属性(活读;未注入/未知键 = null)
            language = AgentConfigView?.Get("language"),
        };

    public override object? HandlePluginData(string? sessionId, string? body) =>
        new { ok = true, echo = body };
}
