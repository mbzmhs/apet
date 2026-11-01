using APet.Framework.Plugin;
using APet.Framework.Session;

namespace APet.Plugins.Scene;

/// <summary>
/// 会话情景插件:把会话注入的情景描述($scene 指令)拼进 system prompt("Current scene: ...")。
/// 情景文本由宿主按会话提供;随会话持久化,多次注入以最后一次为准。
/// </summary>
public sealed class ScenePlugin : AgentPluginBase
{
    /// <summary>由宿主提供:按 AgentSession 返回当前生效的情景文本(无/空 = 不注入)。</summary>
    public Func<AgentSession, string?>? SceneProvider { get; set; }

    /// <summary>$scene 指令执行器(宿主注入);未注入时指令不下发给模型面板。</summary>
    public Func<string?, string, string?>? CommandHandler { get; set; }

    public override string Name => "scene";
    public override void Initialize() { }
    public override void Dispose() { }

    public override string? GetSystemPromptSegment(AgentSession session)
    {
        var scene = SceneProvider?.Invoke(session);
        return string.IsNullOrWhiteSpace(scene) ? null : "Current scene: " + scene;
    }

    public override IReadOnlyList<PluginChatCommand> GetChatCommands() =>
        CommandHandler is null
            ? []
            : [new PluginChatCommand("scene", "按预设情景注入会话提示词($scene 名称);空参数清空", true, CommandHandler)];
}
