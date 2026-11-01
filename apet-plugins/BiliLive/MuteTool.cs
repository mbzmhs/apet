using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Tools;

namespace APet.Plugins.BiliLive;

/// <summary>
/// mute():模型决定不回应直播间事件时调用的静默工具(替代旧 [SKIP] 文本协议,行为与工具调用一致、可审计)。
/// BiliLive 插件在 OnAssistantMessage 中拦截该调用(移除工具调用并清空正文 → 本轮静默结束,
/// 执行循环不会真正运行它);ExecuteAsync 仅在插件未启用时的兜底路径。
/// 生效启用 = 用户开关(工具页) && BiliLive 插件已启用:直播间模式关闭时不向模型暴露该工具。
/// </summary>
public sealed class MuteTool : IApetTool
{
    /// <summary>工具协议名(BiliLive 插件在 OnAssistantMessage 中按此名拦截)。</summary>
    public const string ToolName = "mute";

    private readonly Func<bool> _liveEnabled;
    private bool _userEnabled = true;

    public MuteTool(Func<bool> liveEnabled) => _liveEnabled = liveEnabled;

    /// <summary>用户开关(设置页"工具")与 BiliLive 插件启用状态的合取。</summary>
    public bool Enabled
    {
        get => _userEnabled && _liveEnabled();
        set => _userEnabled = value;
    }

    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        ToolName,
        "Stay silent for this turn: produce no reply to the current live room events. Call it instead of writing a response when an event does not deserve one.",
        """{"type":"object","properties":{},"required":[]}""",
        displayName: "静默");

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default) =>
        Task.FromResult("Muted: this turn produces no reply.");

    public ToolEffect AnalyzeEffect(string argumentsJson) => ToolEffect.NoEffect;
}
