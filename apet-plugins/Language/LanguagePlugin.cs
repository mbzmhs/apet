using APet.Framework.Plugin;
using APet.Framework.Session;

namespace APet.Plugins.Language;

/// <summary>
/// Agent 语言(zh/ja/en):向 system prompt 注入一句"用该语言回复"的指令(用目标语言本身书写,
/// 片段在每轮组装消息时求值,切换语言下一轮即生效);TTS 的 language 参数由宿主从同一设定推送,
/// 保证"回复语言 = 朗读语言"。
/// </summary>
public sealed class LanguagePlugin : AgentPluginBase
{
    /// <summary>语言代码:zh / ja / en(其他值按中文处理)。</summary>
    public string Language { get; set; } = "zh";

    public override string Name => "language";
    public override void Initialize() { }
    public override void Dispose() { }

    public override string? GetSystemPromptSegment(AgentSession session) => Language switch
    {
        "ja" => "必ず日本語で返信してください。",
        "en" => "Please always respond in English.",
        _ => "请使用中文回复。",
    };
}
