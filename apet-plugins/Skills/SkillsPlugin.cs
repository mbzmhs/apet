using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using APet.Framework.Agent;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Security;
using APet.Framework.Session;

namespace APet.Plugins.Skills;

/// <summary>
/// 技能插件:技能 = 文件夹里的 SKILL.md(YAML 头 name/description + Markdown 正文),
/// 目录结构兼容 Codex 技能(便于后续导入)。两个来源:
/// - 内置技能:随插件程序集发布的嵌入资源(Skills/Builtin/*.md,如 new-skill),不出现在用户目录、不可编辑删除;
/// - 用户技能:用户目录 .apet\skills\&lt;技能名&gt;\SKILL.md(同名时用户技能覆盖内置)。
/// 技能索引注入 system prompt;skill 工具(模型按需加载全文);$skills 指令(用户查看清单)。
/// 停用清单由宿主推送(SetDisabledSkills);内容读写/删除供设置页 REST 使用。
/// </summary>
public sealed class SkillsPlugin : AgentPluginBase
{
    public override string Name => "Skills";

    private const string BuiltinResourcePrefix = "APet.Plugins.Skills.Builtin.";

    private volatile HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户技能固定目录:用户目录 .apet\skills(结构与 .codex\skills 同构)。</summary>
    public static string SkillsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".apet", "skills");

    /// <summary>宿主推送停用清单(构造与设置页启停共用)。</summary>
    public void SetDisabledSkills(IEnumerable<string> names) =>
        _disabled = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    // Initialize/Dispose 为抽象成员,必须实现;技能扫描是懒执行,无生命周期资源
    public override void Initialize() { }

    public override void Dispose() { }

    private bool IsDisabled(string name) => _disabled.Contains(name);

    private sealed record SkillInfo(string Name, string Description, string Body, string FilePath, bool Builtin, DateTime? UpdatedAt);

    private static readonly Regex s_frontmatter =
        new(@"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n?(.*)$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>解析 SKILL.md:frontmatter 的 name/description(可带引号)+ 正文;无 frontmatter 时整篇为正文。</summary>
    private static (string Name, string Description, string Body) ParseFrontmatter(string text, string fallbackName)
    {
        var name = fallbackName;
        var description = "";
        var body = text;
        var m = s_frontmatter.Match(text);
        if (m.Success)
        {
            body = m.Groups[2].Value;
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0)
                    continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"', '\'');
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                    name = value;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                    description = value;
            }
        }
        return (name, description, body);
    }

    /// <summary>内置技能(嵌入资源,目录约定与用户技能一致;文件路径为空表示随程序集发布)。</summary>
    private static List<SkillInfo> ScanBuiltin()
    {
        var list = new List<SkillInfo>();
        var assembly = typeof(SkillsPlugin).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(BuiltinResourcePrefix, StringComparison.OrdinalIgnoreCase)
                || !resourceName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var skillName = resourceName[BuiltinResourcePrefix.Length..^3];
                var (name, description, body) = ParseFrontmatter(text, skillName);
                list.Add(new SkillInfo(name, description, body, "", Builtin: true, UpdatedAt: null));
            }
            catch
            {
                // 单个内置资源损坏不影响其余
            }
        }
        return list;
    }

    /// <summary>扫描用户技能目录;目录/单个文件问题不影响其余技能。</summary>
    private List<SkillInfo> ScanUser()
    {
        var list = new List<SkillInfo>();
        var dir = SkillsDirectory;
        try
        {
            if (!System.IO.Directory.Exists(dir))
                return list;
            foreach (var file in System.IO.Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var fallback = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                    var (name, description, body) = ParseFrontmatter(text, fallback);
                    if (name.Length > 0)
                        list.Add(new SkillInfo(name, description, body, file,
                            Builtin: false, UpdatedAt: File.GetLastWriteTime(file)));
                }
                catch
                {
                    // 单个技能文件损坏不影响其余技能
                }
            }
        }
        catch
        {
            // 目录不可读:返回空清单
        }
        return list;
    }

    /// <summary>全部技能:内置在前 + 用户技能(同名用户覆盖内置),按名排序。</summary>
    private List<SkillInfo> ScanAll()
    {
        var all = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in ScanBuiltin())
            all[s.Name] = s;
        foreach (var s in ScanUser())
            all[s.Name] = s; // 同名:用户技能优先
        return all.Values.OrderBy(s => s.Builtin ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────── 设置页 REST 使用的能力(清单/读写/删除) ─────────

    /// <summary>设置页清单条目。</summary>
    public sealed record SkillEntry(string Name, string Description, bool Disabled, bool Builtin, DateTime? UpdatedAt);

    /// <summary>技能全文(编辑用)。</summary>
    public sealed record SkillDetail(string Name, string Description, string Content, bool Builtin);

    public IReadOnlyList<SkillEntry> ListEntries() =>
        ScanAll().Select(s => new SkillEntry(s.Name, s.Description, IsDisabled(s.Name), s.Builtin, s.UpdatedAt)).ToList();

    public SkillDetail? ReadSkill(string name)
    {
        var hit = ScanAll().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : new SkillDetail(hit.Name, hit.Description, hit.Body, hit.Builtin);
    }

    /// <summary>写入用户技能(新建或覆盖)。内容已带 --- frontmatter(如导入的完整 SKILL.md)时原样保存;
    /// 否则 frontmatter 由 name/description 生成。非法名抛异常;内置技能名不可占用。</summary>
    public void WriteSkill(string name, string description, string content)
    {
        if (ScanBuiltin().Any(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("内置技能不可覆盖,请换一个名称");
        var safe = SanitizeName(name);
        var dir = Path.Combine(SkillsDirectory, safe);
        System.IO.Directory.CreateDirectory(dir);
        var text = content;
        if (!text.StartsWith("---"))
        {
            var sb = new StringBuilder("---\n");
            sb.Append("name: ").Append(name.Trim()).Append('\n');
            if (!string.IsNullOrWhiteSpace(description))
                sb.Append("description: ").Append(description.Trim().Replace("\n", " ")).Append('\n');
            sb.Append("---\n\n").Append(content);
            text = sb.ToString();
        }
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), text, new UTF8Encoding(false));
    }

    /// <summary>删除用户技能目录;内置技能不可删除。</summary>
    public static void DeleteSkill(string name)
    {
        var safe = SanitizeName(name);
        var dir = Path.Combine(SkillsDirectory, safe);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, recursive: true);
    }

    /// <summary>技能名 → 目录安全名(允许字母数字-_-与中文,其余以 _ 替代;空白裁剪;空名抛异常)。</summary>
    public static string SanitizeName(string raw)
    {
        var safe = Regex.Replace(raw.Trim(), @"[^A-Za-z0-9\-_\u4e00-\u9fff]", "_");
        if (safe.Length == 0 || safe.All(c => c == '_'))
            throw new ArgumentException("技能名无效");
        return safe;
    }

    // ───────── Agent 侧能力(提示词索引 / skill 工具 / $skills 指令) ─────────

    public override string? GetSystemPromptSegment(AgentSession session)
    {
        var skills = ScanAll().Where(s => !IsDisabled(s.Name)).ToList();
        if (skills.Count == 0)
            return null;
        var sb = new StringBuilder();
        sb.AppendLine("Skills: pre-written instruction packages; the list below is an index only — load full instructions on demand with the skill tool.");
        sb.AppendLine("Available skills:");
        foreach (var s in skills)
        {
            sb.Append("<skill><name>").Append(s.Name).Append("</name>");
            if (s.Description.Length > 0)
                sb.Append("<description>").Append(s.Description).Append("</description>");
            sb.AppendLine("</skill>");
        }
        sb.AppendLine("Rules:");
        sb.AppendLine("- Load a skill only when you are about to use it, then act according to the loaded content.");
        sb.AppendLine("- The skill name must exactly match a <name> above; never invent names.");
        sb.AppendLine("- Each <description> is a one-line summary — load the skill before relying on any detail inside it.");
        return sb.ToString();
    }

    public override bool IsTool => true;

    public override ToolDefinition ToolDefinition => ToolDefinition.Create(
        "skill",
            "Load the full instructions of a skill. Only call it when you need to execute that skill; after loading, act according to the skill content. The name must exactly match one of the skills listed in your system prompt.",
        """{"type":"object","properties":{"name":{"type":"string","description":"Skill name (must match a skill listed in the system prompt)"}},"required":["name"]}""",
        "加载技能");

    /// <summary>skill 工具只读内存索引与技能文件,不产生任何文件系统影响:无害,无需授权询问。</summary>
    public override ToolEffect AnalyzeToolEffect(string argumentsJson) => ToolEffect.NoEffect;

    public override Task<string> ExecuteToolAsync(string argumentsJson, CancellationToken cancellationToken = default)
    {
        string? name = null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("name", out var n))
                name = n.GetString();
        }
        catch
        {
            // 参数非法按未提供处理
        }
        var skills = ScanAll().Where(s => !IsDisabled(s.Name)).ToList();
        var hit = skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return Task.FromResult("技能不存在或已停用:" + (name ?? "(未提供)")
                + "。可用技能:" + (skills.Count > 0 ? string.Join(", ", skills.Select(s => s.Name)) : "(无)"));
        return Task.FromResult(hit.Body);
    }

    public override IReadOnlyList<PluginChatCommand> GetChatCommands() =>
    [
        new PluginChatCommand("skills", "列出可用技能(技能插件)", TakesArgument: false, Execute: (_, _) =>
        {
            var skills = ScanAll().Where(s => !IsDisabled(s.Name)).ToList();
            if (skills.Count == 0)
                return "还没有技能:把技能放到 " + SkillsDirectory + "\\<技能名>\\SKILL.md,或在设置页新建/导入。";
            // Markdown 表格:前端按无卡片 Markdown 块渲染,列对齐看得清
            var sb = new StringBuilder("可用技能(共 " + skills.Count + " 个):\n\n| 技能 | 说明 |\n| --- | --- |");
            foreach (var s in skills)
                sb.Append('\n').Append("| ").Append(s.Name).Append(s.Builtin ? "(内置)" : "")
                  .Append(" | ").Append(s.Description.Length > 0 ? s.Description.Replace("|", "\\|") : "—").Append(" |");
            return sb.ToString();
        }),
    ];
}
