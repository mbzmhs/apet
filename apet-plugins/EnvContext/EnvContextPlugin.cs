using System.Globalization;
using System.Runtime.InteropServices;
using APet.Framework.Plugin;
using APet.Framework.Session;

namespace APet.Plugins.EnvContext;

/// <summary>
/// 环境信息注入:向 system prompt 提供工作目录、操作系统、日期与时区。
/// 工作目录是会话级的,由宿主经 WorkingDirectoryProvider 提供裸路径(未设置则不注入该行);
/// 系统/日期/时区由插件自取。日期只到"日"粒度——时刻每轮都在变,会打穿 system prompt
/// 的前缀缓存;片段每轮运行时求值、不写会话历史,跨天后下一轮即用上新日期。
/// </summary>
public sealed class EnvContextPlugin : AgentPluginBase
{
    /// <summary>由宿主提供:按 AgentSession 返回工作目录路径(未设置时返回 null)。</summary>
    public Func<AgentSession, string?>? WorkingDirectoryProvider { get; set; }

    public override string Name => "env-context";

    public override void Initialize() { }

    public override void Dispose() { }

    public override string? GetSystemPromptSegment(AgentSession session)
    {
        var lines = new List<string> { "Environment:" };
        var cwd = WorkingDirectoryProvider?.Invoke(session);
        if (!string.IsNullOrWhiteSpace(cwd))
            lines.Add($"- Working directory: {cwd}");
        var now = DateTime.Now;
        var tz = TimeZoneInfo.Local;
        var offset = tz.BaseUtcOffset;
        var offsetText = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        lines.Add($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        lines.Add($"- Date: {now:yyyy-MM-dd} ({now.ToString("dddd", CultureInfo.InvariantCulture)})");
        lines.Add($"- Time zone: {tz.Id} ({offsetText})");
        return string.Join("\n", lines);
    }
}
