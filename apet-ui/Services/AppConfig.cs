using System.IO;
using System.Reflection;
using System.Text.Json;
using APet.Framework.Logging;
using APet.Framework.Plugin;
using APet.Framework.Security;

namespace APet.UI.Services;

// ───────── 配置模型 ─────────

/// <summary>单个 Agent 的独立配置(多 Agent 各自一份,互不影响)。</summary>
public sealed class AgentConfig
{
    /// <summary>生成新 Agent Id:agent_ + GUID 前 8 位(初始 Agent 与新增 Agent 统一风格)。</summary>
    public static string NewId() => "agent_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>把本 Agent 的设定持久化到它自己的目录(agents/{Id}/setting.json)。</summary>
    public void Save() => UiConfig.SaveAgent(this);

    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "APet";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>是否启用:仅启用的 Agent 显示在托盘并在启动时初始化。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>启动后自动打开对话窗口(默认不弹,仅可从托盘点开)。</summary>
    public bool AutoOpenOnStartup { get; set; }

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; set; } = "You are APet, a warm and witty desktop companion. Reply in the user's language. Keep casual replies short.";

    public LlmConfig Llm { get; set; } = new();

    /// <summary>达到阈值后自动压缩上下文(AI 总结旧的、保留最近消息);上限取自所选模型的上下文大小。</summary>
    public bool AutoCompress { get; set; } = true;

    /// <summary>压缩触发阈值(占 Token 上限的百分比,10-95)。</summary>
    public int CompressThreshold { get; set; } = 80;

    /// <summary>压缩策略:summarize(AI 总结旧消息,摘要置于消息头部)/ truncate(保留窗口之外直接丢弃,不调用 LLM)。</summary>
    public string CompressStrategy { get; set; } = "summarize";

    /// <summary>压缩保留窗口:最近 x% tokens 的整条消息(占最大上下文百分比,1-90);从最新消息往前累计。</summary>
    public int CompressKeepPercent { get; set; } = 20;

    /// <summary>摘要长度上限:最大上下文的 y%(1-50);经 max_tokens 传给 LLM 约束总结器输出。</summary>
    public int CompressSummaryMaxPercent { get; set; } = 10;

    /// <summary>选定的摘要模板预设:内置预设名(轻量通用/编程开发/文章写作/角色扮演)或 "__custom__"(自定义);
    /// 空 = 旧版数据,按 CompressTemplate 是否为空回退为自定义或缺省。</summary>
    public string CompressPreset { get; set; } = "";

    /// <summary>用户自定义的摘要模板正文(仅预设选"自定义"时生效;内置预设不读此字段,故切换预设不会覆盖它)。
    /// token 预算说明自动前置在模板之前。留空用内置缺省。</summary>
    public string CompressTemplate { get; set; } = "";

    /// <summary>Agent 语言:zh / ja / en。决定注入 system prompt 的回复语言,并作为 TTS 调用的 language 参数。</summary>
    [PluginVisible("language")]
    public string Language { get; set; } = "zh";

    /// <summary>隐藏思考内容(仅显示层):聊天界面不展示助手思考块;持久化记录仍全量保存思考内容,可随时切换。</summary>
    public bool HideThinking { get; set; }

    /// <summary>隐藏情绪 Tag(默认开):流式显示时自动剥离情绪标签(与 TTS/气泡同规则),入库的 Display 不含这些标签;Context 照常记录原文。</summary>
    public bool HideEmotionTags { get; set; } = true;

    /// <summary>工具调用最大循环轮数;0 = 不限制(上限 1000)。</summary>
    public int MaxToolIterations { get; set; } = 0;

    public double? Temperature { get; set; }

    public SecurityConfig Security { get; set; } = new();

    /// <summary>工具启用状态(name → enabled),未记录的工具默认启用。</summary>
    public Dictionary<string, bool> ToolEnabled { get; set; } = new(StringComparer.Ordinal);

    /// <summary>会话列表置顶的会话 Id(顺序无关;前端按此分组置顶显示)。</summary>
    public List<string> PinnedSessions { get; set; } = new();

    /// <summary>会话列表收起状态的文件夹 Id(前端展开/收起切换时整表上报,跨重启恢复;
    /// 文件夹被删时由服务端顺手剔除,无需迁移)。</summary>
    public List<string> CollapsedFolderIds { get; set; } = new();

    /// <summary>最近打开(浏览)的会话 Id:聊天窗下次开窗优先回到它(前端每次会话上屏时上报);
    /// 空或该会话已删/归档时,开窗回落到最近活跃的会话。</summary>
    public string LastSessionId { get; set; } = "";

    /// <summary>
    /// 终端 shell:bash(Git-Bash)/ cmd / powershell(默认 powershell)。
    /// 统一供「运行终端」(bash)工具与后台 bg_run 使用;空值视为 powershell。
    /// </summary>
    public string TerminalShell { get; set; } = "powershell";

    // ───────── 插件与界面设置(每个 Agent 独立一份,互不影响) ─────────
    public TtsConfig Tts { get; set; } = new();
    public BiliLiveConfig BiliLive { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public DesktopPetConfig DesktopPet { get; set; } = new();

    /// <summary>预设情景库($scene 指令按名注入会话;按 Agent 保存,全部会话共用)。</summary>
    public List<ScenePreset> ScenePresets { get; set; } = new();

    /// <summary>情景插件总开关:关闭即停用 $scene 指令与情景注入。</summary>
    public bool SceneEnabled { get; set; } = true;

    /// <summary>聊天区背景图(中间聊天区域;左右面板与标题栏保持实底)。</summary>
    public BackgroundConfig ChatBackground { get; set; } = new();

    /// <summary>MCP 服务器连接配置(stdio):服务器工具以 mcp_{服务器}_{工具} 代理注册进 Agent。</summary>
    public McpConfig Mcp { get; set; } = new();

    /// <summary>技能插件配置:技能启停清单(技能目录固定为用户目录 .apet\skills)。</summary>
    public SkillsConfig Skills { get; set; } = new();

    /// <summary>子代理插件配置:总开关 + 子代理定义清单(设置页建立:名称/系统提示词/工具集)。</summary>
    public SubAgentConfig SubAgents { get; set; } = new();

    /// <summary>整个窗体背景图:铺在所有面板之下,聊天区透出显示(聊天区自己的背景图优先生效)。</summary>
    public BackgroundConfig WindowBackground { get; set; } = new();

    /// <summary>对话气泡偏移(像素):微调相对宠物窗口的锚定位置,供模型预留空白过大时修正。</summary>
    public ChatBubbleConfig ChatBubble { get; set; } = new();

    /// <summary>
    /// 外部插件(plugins 目录 DLL)的持久化设置袋:键 = 插件 Name;
    /// Values 与 [PluginSetting] 属性同名(不分大小写),经 TrySetSettings 校验后灌入插件实例。
    /// </summary>
    public Dictionary<string, ExternalPluginSettings> PluginSettings { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>界面缩放因子(0.75-2.0,1.0=默认);本 Agent 的对话窗口独立缩放。</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>UI 面板不透明度百分比(0-100,默认 80):侧边栏/顶栏/输入卡片/设置卡片的底色透明度,越低背景图越明显。</summary>
    public int PanelOpacityPercent { get; set; } = 80;

    /// <summary>Web 主窗渲染模式(WebView2):sw=全软件渲染(默认,多显卡/虚拟显示器下最稳) / cpu=CPU 合成 / hw=硬件加速 GPU 渲染。窗口重开生效。</summary>
    public string WebViewRenderMode { get; set; } = "sw";

    /// <summary>聊天窗口上次位置 X(null = 未保存过,启动居中;拖动后自动落盘,下次恢复)。</summary>
    public double? ChatPosX { get; set; }

    /// <summary>聊天窗口上次位置 Y(null = 未保存过,启动居中;拖动后自动落盘,下次恢复)。</summary>
    public double? ChatPosY { get; set; }

    /// <summary>聊天窗口上次宽度(null = 未保存过,用默认 1440×900)。</summary>
    public double? ChatWidth { get; set; }

    /// <summary>聊天窗口上次高度(null = 未保存过,用默认 1440×900)。</summary>
    public double? ChatHeight { get; set; }

    /// <summary>
    /// 确保全部嵌套配置对象非 null:旧版本/手改配置可能把子对象写成 null(如 DesktopPet.Live2D),
    /// 反序列化后原样保留,运行期与 Web 序列化都会踩空。在加载与 Web 读写前调用。
    /// </summary>
    public void EnsureInitialized()
    {
        Llm ??= new();
        Security ??= new();
        Tts ??= new();
        Tts.Common ??= new();
        Tts.Server ??= new();
        Tts.Windows ??= new();
        Tts.Gsv ??= new();
        BiliLive ??= new();
        Memory ??= new();
        DesktopPet ??= new();
        DesktopPet.Spine ??= new();
        DesktopPet.Live2D ??= new();
        DesktopPet.Vrm ??= new();
        DesktopPet.Basic ??= new();
        ChatBubble ??= new();
        ChatBackground ??= new();
        WindowBackground ??= new();
        ToolEnabled ??= new(StringComparer.Ordinal);
        ScenePresets ??= new();
        PluginSettings ??= new(StringComparer.Ordinal);
        foreach (var bag in PluginSettings.Values)
            bag.Values ??= new(StringComparer.OrdinalIgnoreCase);
        Mcp ??= new();
        Mcp.Servers ??= new();
        Skills ??= new();
        SubAgents ??= new();
        SubAgents.Agents ??= new();
    }
}

/// <summary>
/// 标注 AgentConfig 属性对外部插件可见:标注后的属性值可经插件实例的
/// AgentConfigView(框架侧 IAgentConfigView)按键名读取。
/// 键名是对外契约(被插件使用过即不得改名);此注解是安全决策,
/// 仅可对确认可以暴露给外部插件的属性标注。
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PluginVisibleAttribute : Attribute
{
    public string Key { get; }
    public PluginVisibleAttribute(string key) => Key = key;
}

/// <summary>
/// 暴露给插件的 AgentConfig 只读视图(框架侧 IAgentConfigView 的宿主实现):
/// 宿主构造后注入每个插件实例的 AgentConfigView。
/// 值每次从 AgentConfig 实例活读——设置页保存就地修改同一实例,插件始终读到最新值。
/// </summary>
public sealed class PluginConfigView : IAgentConfigView
{
    private static readonly JsonSerializerOptions s_json = new();
    private static readonly Lazy<Dictionary<string, PropertyInfo>> s_props = new(() =>
        typeof(AgentConfig).GetProperties()
            .Select(p => (p, attr: p.GetCustomAttribute<PluginVisibleAttribute>()))
            .Where(x => x.attr != null)
            .ToDictionary(x => x.attr!.Key, x => x.p));

    private readonly AgentConfig _config;

    public PluginConfigView(AgentConfig config) => _config = config;

    public IReadOnlyCollection<string> Keys => s_props.Value.Keys;

    public object? Get(string key) =>
        s_props.Value.TryGetValue(key, out var prop) ? prop.GetValue(_config) : null;

    public string? GetJson(string key)
    {
        var value = Get(key);
        return value is null ? null : JsonSerializer.Serialize(value, s_json);
    }
}

/// <summary>MCP 插件配置:多台 stdio 服务器;保存后由宿主按差异断开/重连。</summary>
public sealed class McpConfig
{
    /// <summary>MCP 插件总开关(关闭即断开全部服务器并注销其工具)。</summary>
    public bool Enabled { get; set; } = true;

    public List<McpServerConfig> Servers { get; set; } = new();
}

/// <summary>一台 MCP 服务器的启动配置(stdio / http / sse 三种传输)。</summary>
public sealed class McpServerConfig
{
    /// <summary>服务器名(工具前缀 mcp_{名}_{工具});留空时用 Command/Url 充当。</summary>
    public string Name { get; set; } = "";

    /// <summary>传输类型:stdio(本地命令)/ http(Streamable HTTP)/ sse(HTTP+SSE 旧协议)。</summary>
    public string Type { get; set; } = "stdio";

    /// <summary>stdio:启动命令(如 npx / python / 完整 exe 路径)。</summary>
    public string Command { get; set; } = "";

    /// <summary>stdio:命令行参数(原样交给子进程,空格分隔)。</summary>
    public string Arguments { get; set; } = "";

    /// <summary>http/sse:服务器 URL。</summary>
    public string Url { get; set; } = "";

    /// <summary>stdio:环境变量(JSON 对象字符串,可选)。</summary>
    public string EnvJson { get; set; } = "";

    /// <summary>http/sse:附加请求头(JSON 对象字符串,如 {"Authorization":"Bearer ghp_xxx"}),用于 PAT/API 密钥鉴权。</summary>
    public string HeadersJson { get; set; } = "";

    /// <summary>stdio:工作目录;留空用部署目录。</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>是否启用该服务器。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>单次请求超时毫秒数(默认 30000)。</summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>协议版本:auto(默认,使用 2024-11-05)或指定版本号。</summary>
    public string ProtocolVersion { get; set; } = "auto";

    /// <summary>信任此服务器的工具:调用不再询问授权(仅对完全信任的服务器开启;MCP 注解 readOnlyHint 的工具本就自动放行)。</summary>
    public bool TrustTools { get; set; }
}

/// <summary>技能插件配置:技能固定存放于用户目录 .apet\skills;此处记录总开关与停用清单。</summary>
public sealed class SkillsConfig
{
    /// <summary>插件总开关:关闭即下线 skill 工具、$skills 指令与提示词索引。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>已停用的技能名列表(未列出的技能默认启用)。</summary>
    public List<string> DisabledSkills { get; set; } = new();
}

/// <summary>子代理插件配置:总开关 + 子代理定义(设置页建立;保存后由宿主热应用到插件)。</summary>
public sealed class SubAgentConfig
{
    /// <summary>插件总开关:关闭即下线 sub_* 工具与 system prompt 指引(在跑的子代理不受影响)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>子代理定义清单(sub_run 按名称引用;启用且名称非空才可用)。</summary>
    public List<SubAgentDefConfig> Agents { get; set; } = new();
}

/// <summary>一个子代理定义:名称 + 系统提示词 + 工具集(从内置基础工具勾选)。</summary>
public sealed class SubAgentDefConfig
{
    /// <summary>子代理名称(同 Agent 内唯一;sub_run 按名引用;设置页校验)。</summary>
    public string Name { get; set; } = "";

    /// <summary>子代理系统提示词(角色与工作方式;它看不到主对话,任务上下文全由 sub_run 的 prompt 提供)。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>工具集(勾选;限内置基础工具:read_file / write_file / edit_file / bash / web_fetch)。</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>子代理供应商(空 = 继承主会话所选供应商;显式指定时模型必随之指定)。</summary>
    public string Provider { get; set; } = "";

    /// <summary>子代理模型(空 = 继承主会话所选模型;供应商取本条目的 Provider,未指定时随主会话/Agent 默认)。</summary>
    public string Model { get; set; } = "";

    /// <summary>子代理思考等级(空 = 继承主会话所选等级;取值与聊天界面档位一致: none/default/minimal/low/medium/high/xhigh/max)。</summary>
    public string ThinkingEffort { get; set; } = "";

    /// <summary>子代理工作目录权限(只读 ReadOnly/读写 ReadWrite/完全访问 FullAccess/全部询问 Ask;
    /// 默认只读。工作目录本身仍继承主会话,此级别单独钳制子代理在该目录内的操作权限;
    /// 其他目录与白名单仍随主会话策略)。</summary>
    public string WorkingDirectoryAccess { get; set; } = "ReadOnly";

    /// <summary>是否启用该定义(停用 = sub_run 不可引用,工具配置保留)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>该子代理最大工具循环轮数(0 = 不限,任务跑完为止;0-1000)。</summary>
    public int MaxToolIterations { get; set; } = 0;
}

/// <summary>一个外部插件的持久化设置:启停状态 + 全部设置项的当前值(JSON 通用承载)。</summary>
public sealed class ExternalPluginSettings
{
    /// <summary>是否启用(未记录过时默认 true)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>设置项当前值;键与 [PluginSetting] 名称对应,值以 JSON 通用形式承载。</summary>
    public Dictionary<string, JsonElement?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一条供应商连接:名称 / BaseURL / APIKey(相互对应)+ 该供应商的模型与上下文大小。</summary>
public sealed class ProviderConfig
{
    /// <summary>BaseURL 缺省值(框架要求非空;可在设置页改为实际地址)。</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:8080/v1";

    /// <summary>用户可见名称,列表内唯一(重名自动加数字后缀)。</summary>
    public string Name { get; set; } = "";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ApiKey { get; set; } = "";

    /// <summary>该供应商上次选用的模型(切换供应商时自动带回;也是会话未单独选模型时的默认)。</summary>
    public string Model { get; set; } = "";

    /// <summary>思考等级:none(关闭)/ default(开启,按 high 发送)/ minimal / low / medium / high / xhigh / max,随供应商保存;新建供应商默认 high。</summary>
    public string ReasoningEffort { get; set; } = "high";

    /// <summary>是否在请求体携带 thinking 字段(GLM 等端点以它控制思考开关);
    /// Gemini 的 OpenAI 兼容端点不支持该字段,应保持 false。随供应商保存。
    /// 旧版开关,由 ThinkingField 取代:旧配置无 ThinkingField 时按它映射(true→thinking,false→off)。</summary>
    public bool SendThinkingField { get; set; } = true;

    /// <summary>思考字段样式:thinking(默认,GLM/阿里百炼等)/ enable_thinking(Qwen 原生)/
    /// reasoning_effort(OpenAI、llama.cpp 等,携带具体强度)/ off(不发送,Gemini 兼容等严格端点)。
    /// 空 = 未配置,按旧开关 SendThinkingField 映射。</summary>
    public string ThinkingField { get; set; } = "";

    /// <summary>该供应商生效的思考字段样式(ThinkingField 优先;旧配置按 SendThinkingField 映射;再默认 thinking)。</summary>
    public string EffectiveThinkingField
    {
        get
        {
            var f = (ThinkingField ?? "").Trim().ToLowerInvariant();
            if (f.Length > 0)
                return f;
            return SendThinkingField ? "thinking" : "off";
        }
    }

    /// <summary>把任意思考强度取值归一化为 none/default/minimal/low/medium/high/xhigh/max;
    /// default = 开启(上线按 high 发送);无法识别(空值/大小写差异/旧版值)一律按 none 关闭。
    /// 供「界面显示」与「实际发给模型」共用,保证两者始终一致(避免显示 none 却仍思考)。</summary>
    public static string NormalizeReasoningEffort(string? value)
    {
        var e = (value ?? "").Trim().ToLowerInvariant();
        return e is "none" or "default" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" ? e : "none";
    }

    /// <summary>用户选定的模型清单:聊天界面只能从这里选择;每个模型有独立的上下文大小与 MaxTokens。
    /// 清单完全由用户维护(新供应商为空,不自动添加)。</summary>
    public List<ProviderModelConfig> Models { get; set; } = new();

    /// <summary>按名称查找清单中的模型(忽略大小写);不存在返回 null。</summary>
    public ProviderModelConfig? FindModel(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>供应商模型清单中的一项:聊天可用模型及其独立参数。</summary>
public sealed class ProviderModelConfig
{
    /// <summary>模型名(发给 API 的 model 值)。</summary>
    public string Name { get; set; } = "";

    /// <summary>该模型的上下文大小(Token);须为大于 0 的整数(设置页强制校验,默认 65536)。</summary>
    public int ContextTokens { get; set; } = 65536;

    /// <summary>本地手动值优先于服务端上报的上下文大小(默认);false = 优先服务端上报值(取不到回退本地)。</summary>
    public bool PreferLocal { get; set; } = true;

    /// <summary>该模型的 MaxTokens(单次回复上限);0 = 不限制(请求不携带 max_tokens)。</summary>
    public int MaxTokens { get; set; }

    /// <summary>该模型可用的思考等级(取值为 none/minimal/low/medium/high/xhigh/max 的子集);
    /// 空 = 全部 7 档可选。聊天界面的思考下拉按此过滤。</summary>
    public List<string> ThinkingLevels { get; set; } = new();

    /// <summary>该模型支持的附件媒体类型:image / video / audio / pdf(文本与通用文件始终可用);
    /// 空 = 沿用默认(仅图片)。聊天框 ＋ 菜单的文件类型按此显示。</summary>
    public List<string> MediaTypes { get; set; } = new();

    // ───────── 计费因子(元/百万 tokens;0 = 该项不计费,本地/免费模型全 0) ─────────

    /// <summary>输入价格(元/百万输入 tokens)。缓存命中部分按 PriceInputCached 计费。</summary>
    public double PriceInput { get; set; }

    /// <summary>缓存输入价格(元/百万缓存命中 tokens)。</summary>
    public double PriceInputCached { get; set; }

    /// <summary>输出价格(元/百万输出 tokens;思考 tokens 同价)。</summary>
    public double PriceOutput { get; set; }
}

public sealed class LlmConfig
{
    /// <summary>供应商列表;列表内选中项(ActiveProvider)即为当前使用的连接。</summary>
    public List<ProviderConfig> Providers { get; set; } = new();

    /// <summary>当前使用的供应商名(对应 Providers 中的 Name)。</summary>
    public string ActiveProvider { get; set; } = "";

    /// <summary>代理模式:system(使用系统设定)/ none(不使用)/ custom(自定义)。</summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>自定义代理地址(ProxyMode=custom 时使用)。</summary>
    public string ProxyUrl { get; set; } = "";

    /// <summary>当前生效的供应商;列表为空时补一条"自定义",名称失配时回退到第一项。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProviderConfig Active
    {
        get
        {
            if (Providers.Count == 0)
                Providers.Add(new ProviderConfig { Name = "自定义" });
            return Providers.FirstOrDefault(p =>
                       string.Equals(p.Name, ActiveProvider, StringComparison.OrdinalIgnoreCase))
                   ?? Providers[0];
        }
    }

    /// <summary>按名称查找供应商(忽略大小写)。</summary>
    public ProviderConfig? Find(string? name) => Providers.FirstOrDefault(p =>
        string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>取列表内唯一名称:与现有项重名(忽略大小写)时追加数字后缀,空值回退"自定义"。</summary>
    public string UniqueName(string desired)
    {
        var name = desired.Trim();
        if (name.Length == 0)
            name = "自定义";
        if (Find(name) is null)
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = name + i;
            if (Find(candidate) is null)
                return candidate;
        }
    }
}

/// <summary>
/// Agent 级工具权限(默认工作目录 + 其他目录分级 + 白名单)。
/// 工作目录及其权限是会话级设定,由每个 Session 自己提供;Agent 级只提供新建会话的默认值。
/// </summary>
public sealed class SecurityConfig
{
    /// <summary>默认工作目录:新建会话自动继承(空 = 不预置,会话自行设置)。</summary>
    public string DefaultWorkingDirectory { get; set; } = "";

    /// <summary>临时工作区根目录:未设工作目录的会话在首次运行前,于此按会话自动创建工作目录
    /// (空 = 用默认 %LOCALAPPDATA%\APet\workspaces)。命令的 cwd 因此不再落到应用安装目录。</summary>
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>默认工作目录权限:ReadOnly / ReadWrite / FullAccess / Ask(新建会话随目录一并继承)。</summary>
    public string DefaultWorkingDirectoryAccess { get; set; } = "ReadWrite";

    /// <summary>白名单目录列表(任何操作都放行)。</summary>
    public List<string> Whitelist { get; set; } = new();
}

/// <summary>TTS 插件配置:Common = 引擎无关的通用设置;三个引擎各自子对象。</summary>
public sealed class TtsConfig
{
    public TtsCommonConfig Common { get; set; } = new();
    public TtsServerConfig Server { get; set; } = new();
    public TtsWindowsConfig Windows { get; set; } = new();
    public TtsGsvConfig Gsv { get; set; } = new();
}

/// <summary>TTS 通用设置(引擎无关)。</summary>
public sealed class TtsCommonConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>合成引擎:server(自定义 HTTP 服务)/ windows(微软系统语音)/ gsv(GSV-TTS 本地推理)。</summary>
    public string Engine { get; set; } = "server";

    /// <summary>朗读时机:PerSentence(逐句)/ AfterReply(整段回复后)。</summary>
    public string Timing { get; set; } = "PerSentence";

    /// <summary>不朗读内心想法:圆括号(全角/半角)内文本视为内心独白跳过朗读。</summary>
    public bool SkipInnerThought { get; set; } = true;

    /// <summary>语速倍率(0.25~4.0;server/gsv 作为请求参数,windows 映射为 SAPI Rate)。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>播放音量(0~1,默认 1):TTS 播放器(NAudio WASAPI)的输出音量,对三种引擎统一生效。</summary>
    public double Volume { get; set; } = 1.0;
}

/// <summary>自定义服务器引擎(server)设置。</summary>
public sealed class TtsServerConfig
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:9883";

    /// <summary>TTS 角色/模型(对应服务端 /v1/models 的 id;空 = 服务端当前常驻角色)。</summary>
    public string Model { get; set; } = "";

    /// <summary>音色:请求 voice 参数(空 = 服务端默认音色)。</summary>
    public string Voice { get; set; } = "neutral";

    public string ResponseFormat { get; set; } = "wav";
}

/// <summary>Windows 系统语音引擎(windows)设置。</summary>
public sealed class TtsWindowsConfig
{
    /// <summary>系统语音角色名(空 = 系统默认角色)。</summary>
    public string SapiVoice { get; set; } = "";
}

/// <summary>GSV 本地推理引擎(gsv)设置。</summary>
public sealed class TtsGsvConfig
{
    /// <summary>默认声线(名或 名/情绪,如 youxiang 或 youxiang/happy);对话中情绪标签临时切换,
    /// 一轮回复结束自动回到此默认。留空 = 引擎默认(第一个已配置声线)。声线在下方 Voices 中配置。</summary>
    public string Voice { get; set; } = "";

    /// <summary>模型包目录(含 manifest.json)。</summary>
    public string ModelDir { get; set; } = "";

    /// <summary>计算设备:auto / gpu / cpu(gpu 需 cuDNN,缺失自动降级 CPU)。</summary>
    public string Device { get; set; } = "auto";

    /// <summary>标点停顿基长(秒):每个切块合成后追加的真实静音 = 此值 ÷ 语速 ×
    /// 尾标点缩放(句读 1.5 / 顿号 0.8 / 其余 1.0)。逗号、句号停顿的本体。</summary>
    public double CutMute { get; set; } = 0.3;

    /// <summary>子句合并阈值(语义字数,中日字=1、英文单词=1.75):累积到阈值才切块。
    /// 默认 0 = 每个标点都切块;调大可减少切块、降低合成耗时。</summary>
    public double CutMinlen { get; set; } = 0;

    /// <summary>已配置声线(声线 = 模型名 + 参考列表;设置页创建/编辑)。</summary>
    public List<GsvVoiceSetting> Voices { get; set; } = new();
}

/// <summary>GSV 声线:一个音色模型名 + 参考音频列表(每条 = 情绪 + wav + 文本 + 语言)。</summary>
public sealed class GsvVoiceSetting
{
    public string Name { get; set; } = "";

    /// <summary>SoVITS 模型目录名(模型包 voices/ 下;空 = 底模)。</summary>
    public string Sovits { get; set; } = "";

    /// <summary>GPT 模型目录名(模型包 gpt/ 下;空 = 底模)。</summary>
    public string Gpt { get; set; } = "";

    /// <summary>声线级采样参数(null = 跟随全局 TTS 采样设置)。</summary>
    public GsvSamplingSetting? Sampling { get; set; }

    public List<GsvRefSetting> Refs { get; set; } = new();
}

/// <summary>GSV 声线级采样参数(GPT 采样 + flow 噪声)。</summary>
public sealed class GsvSamplingSetting
{
    public double Temperature { get; set; } = 1.0;
    public int TopK { get; set; } = 15;
    public double TopP { get; set; } = 1.0;
    public double RepetitionPenalty { get; set; } = 1.35;
    public double NoiseScale { get; set; } = 0.5;
}

/// <summary>GSV 声线的单条参考音频(情绪预设)。</summary>
public sealed class GsvRefSetting
{
    public string Emotion { get; set; } = "neutral";
    public string Wav { get; set; } = "";
    public string Text { get; set; } = "";
    public string Lang { get; set; } = "zh";
}

public sealed class BiliLiveConfig
{
    /// <summary>插件总开关:关闭即断开直播间连接并停用 $blive 绑定(工具页 mute 随插件启停)。</summary>
    public bool Enabled { get; set; } = true;

    public string RoomCode { get; set; } = "";
    public string Cookie { get; set; } = "";

    /// <summary>回应的事件类型(与插件 [PluginSetting] 同名,持久化后重启不丢)。</summary>
    public bool RespondDanmaku { get; set; } = true;
    public bool RespondGift { get; set; } = true;
    public bool RespondSc { get; set; } = true;
    public bool RespondInteract { get; set; } = true;

    /// <summary>屏蔽关键词(昵称或内容含任一则不回应)。</summary>
    public string BlockKeywords { get; set; } = "";

    /// <summary>合并窗口(毫秒):窗口期内到达的弹幕合并为一轮回复。</summary>
    public int MergeWindowMs { get; set; } = 1500;

    /// <summary>最小间隔(毫秒):两轮直播间回复之间的最小间隔(刷屏限频)。</summary>
    public int MinIntervalMs { get; set; } = 2000;

    /// <summary>事件队列容量(1-500):满时新弹幕/互动丢弃,礼物/SC 挤掉最旧的弹幕。</summary>
    public int MaxQueue { get; set; } = 32;

    /// <summary>直播轮工具白名单:B站插件触发的一轮对话中仅这些工具可被调用(空 = 不限制)。</summary>
    public List<string> ToolWhitelist { get; set; } = new();

    /// <summary>$blive 绑定的会话 Id(空 = 未绑定)。随设定持久化、启动恢复;会话归档/删除/不存在时宿主解绑并清空。</summary>
    public string BoundSessionId { get; set; } = "";
}

/// <summary>记忆插件设置:单 Agent 长期记忆(SQLite,全部会话共享),存于 agents/{Id}/memory.db。</summary>
public sealed class MemoryConfig
{
    /// <summary>插件总开关:关闭即停用记忆召回注入并禁用全部 mem_* 工具。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>自动忘记 N 天未使用过的记忆(0=永不忘记)。</summary>
    public int ForgetAfterDays { get; set; }
}

/// <summary>一个预设情景:$scene 指令按名注入会话提示词;名称唯一(重名时先建者优先生效)。
/// 实现 INPC:设置页下拉框以 Name 作显示项,改名后界面即时同步。</summary>
public sealed class ScenePreset : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private string _content = "";

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>情景描述文本(注入 system prompt 的内容)。</summary>
    public string Content
    {
        get => _content;
        set
        {
            _content = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>背景图设置(聊天区/窗体各一份):图片路径 + 不透明度 + 缩放策略;路径为空 = 不使用背景图。</summary>
public sealed class BackgroundConfig
{
    /// <summary>图片文件完整路径(png/jpg 等);空或文件不存在时不应用。</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>不透明度百分比(0-100);0 = 完全透明(等同关闭)。</summary>
    public int OpacityPercent { get; set; } = 20;

    /// <summary>缩放策略:Fill / Uniform / UniformToFill / None / Tile。</summary>
    public string Stretch { get; set; } = "UniformToFill";
}

/// <summary>各引擎独立的模型设定基类:两种引擎实现表情/动作的机制不同,模型路径与映射互不通用。</summary>
public abstract class PetModelConfig
{
    /// <summary>模型文件路径(Spine:.skel/.json 骨架;Live2D:.model3.json)。</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>待机动作列表(多个时按定时轮换;[idle] 语义)。</summary>
    public List<string> IdleMotions { get; set; } = new();

    /// <summary>情感映射:tag(如 happy)→ 动作/表情名列表(随机选一播放)。tag 由 TTS 解析后发送。
    /// Live2D 优先映射表情名(参数级叠层,不占动作轨),Spine 映射动作名。</summary>
    public Dictionary<string, List<string>> EmotionMap { get; set; } = new();
}

/// <summary>Spine 引擎的模型设定。</summary>
public sealed class SpinePetConfig : PetModelConfig
{
    /// <summary>Spine 图集文件覆盖(空 = 在骨架旁自动查找)。</summary>
    public string AtlasPath { get; set; } = "";

    /// <summary>眨眼动作名(空 = 不眨眼);待机时周期性播放一次(不循环)。</summary>
    public string BlinkMotion { get; set; } = "";
}

/// <summary>Live2D 引擎的模型设定。眨眼由内建 AutoEyeBlink 处理,无眨眼动作项。</summary>
public sealed class Live2DPetConfig : PetModelConfig
{
    /// <summary>
    /// 兜底部件不透明度(部件 Id → 0..1):模型加载完成后应用,作为默认形态
    /// (如隐藏第二对手臂)。自定义表情淡出会精确还原到该基线而非 setup 姿态,任何时刻不露馅。
    /// </summary>
    public Dictionary<string, float> PartOpacities { get; set; } = new();

    /// <summary>内建自动眨眼(CubismAutoEyeBlink),默认开启。</summary>
    public bool AutoEyeBlink { get; set; } = true;

    /// <summary>呼吸动画(周期性身体起伏),默认开启。</summary>
    public bool BreathEnabled { get; set; } = true;
}

/// <summary>VRM 引擎的模型设定:.vrm 文件路径;"动作" = VRM 表情 key + 已导入的 VRMA 动画。</summary>
public sealed class VrmPetConfig : PetModelConfig
{
    /// <summary>渲染风格:mtoon(官方)/ toon(二次元)/ pbr(写实)。</summary>
    public string ShadingStyle { get; set; } = "mtoon";

    /// <summary>已导入的 VRMA 动作文件绝对路径列表(情绪映射里的动作名 = 文件基名)。</summary>
    public List<string> AnimationPaths { get; set; } = new();
}

/// <summary>
/// 基础引擎的模型设定:模型 = 图片文件夹,每个 png/gif 文件是一个动作(文件名含扩展名,
/// gif 展示总时长、png 静态无时长)。无图集/眨眼概念。
/// </summary>
public sealed class BasicPetConfig : PetModelConfig
{
    /// <summary>切换动作图片时的淡入淡出过渡(默认关闭)。</summary>
    public bool FadeTransition { get; set; }

    /// <summary>淡入淡出过渡时长(毫秒)。</summary>
    public int FadeDurationMs { get; set; } = 250;
}

/// <summary>桌宠显示插件配置(引擎/交互为共享项;模型与映射按引擎各自独立)。</summary>
public sealed class DesktopPetConfig
{
    public bool Enabled { get; set; }

    /// <summary>渲染引擎:spine / live2d / basic。</summary>
    public string Engine { get; set; } = "spine";

    /// <summary>显示缩放因子(屏幕像素 / 模型单位,1 = 模型原始尺寸)。</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>悬浮窗左上角 X 坐标(持久化:启动时恢复到上次拖动位置;未拖动过为默认 450)。</summary>
    public int PosX { get; set; } = 450;

    /// <summary>悬浮窗左上角 Y 坐标(持久化,同 PosX;未拖动过为默认 100)。</summary>
    public int PosY { get; set; } = 100;

    /// <summary>待机动作触发间隔(秒):每隔约该时长轮换一个待机动作。</summary>
    public int IdleIntervalSeconds { get; set; } = 10;

    /// <summary>待机触发的随机前后差(秒):实际间隔 = 间隔 ± 该值。</summary>
    public int IdleJitterSeconds { get; set; } = 3;

    /// <summary>眨眼触发间隔(秒,仅 Spine 引擎;Live2D 用内建自动眨眼)。</summary>
    public int BlinkIntervalSeconds { get; set; } = 4;

    /// <summary>眨眼的随机前后差(秒):实际间隔 = 间隔 ± 该值。</summary>
    public int BlinkJitterSeconds { get; set; } = 2;

    /// <summary>情感 tag 包裹风格:SquareBrackets / CurlyBraces / AngleBrackets / DoubleSquareBrackets。决定系统提示词格式并同步给 TTS。不含半角圆括号(与 TTS"不朗读内心想法"冲突)。</summary>
    public string TagStyle { get; set; } = "SquareBrackets";

    /// <summary>单击/双击/右键响应:None / RandomMotion / Idle / OpenChat。</summary>
    public string ClickAction { get; set; } = "OpenChat";
    public string DoubleClickAction { get; set; } = "RandomMotion";
    public string RightClickAction { get; set; } = "None";

    /// <summary>Spine 引擎的模型设定(引擎独立的模型路径/图集/眨眼/映射)。</summary>
    public SpinePetConfig Spine { get; set; } = new();

    /// <summary>Live2D 引擎的模型设定(引擎独立的模型路径/映射)。</summary>
    public Live2DPetConfig Live2D { get; set; } = new();

    /// <summary>VRM 引擎的模型设定(.vrm 模型路径/表情映射)。</summary>
    public VrmPetConfig Vrm { get; set; } = new();

    /// <summary>基础引擎的模型设定(图片文件夹;每个 png/gif 为一个动作)。</summary>
    public BasicPetConfig Basic { get; set; } = new();

    /// <summary>当前引擎对应的模型子配置(引擎切换后各自记忆,互不覆盖)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PetModelConfig CurrentModel =>
        string.Equals(Engine, "live2d", StringComparison.OrdinalIgnoreCase) ? Live2D
        : string.Equals(Engine, "vrm", StringComparison.OrdinalIgnoreCase) ? Vrm
        : string.Equals(Engine, "basic", StringComparison.OrdinalIgnoreCase) ? Basic
        : Spine;
}

/// <summary>
/// 对话气泡设置:锚定偏移(像素,微调气泡相对宠物窗口的位置,供模型预留空白过大时修正;
/// 正值一律朝宠物窗口方向移动:上=下移、左=右移、右=左移,范围 ±300)+ 外观样式
/// (字体/颜色/边框/圆角/尾巴/最大宽/不透明度)。样式字段与 <see cref="Plugins.ChatBubble.ChatBubblePlugin"/>
/// 的 [PluginSetting] 同名,经 PluginConfigBridge 推送;默认值取自 BubbleStyle,三处一致。
/// </summary>
public sealed class ChatBubbleConfig
{
    /// <summary>上偏移:上方放置时整体上下平移,侧置时决定顶端相对宠物顶边的对齐。</summary>
    public int TopOffset { get; set; }

    /// <summary>左偏移:气泡侧置于宠物左侧时生效(内侧边相对宠物左边平移)。</summary>
    public int LeftOffset { get; set; }

    /// <summary>右偏移:气泡侧置于宠物右侧时生效(内侧边相对宠物右边平移)。</summary>
    public int RightOffset { get; set; }

    // ───────── 样式 ─────────

    /// <summary>气泡字体(系统字体名;不存在时渲染回退默认字体)。</summary>
    public string FontFamily { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultFontFamily;

    /// <summary>字号(磅,6–48)。</summary>
    public double FontSize { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultFontSize;

    /// <summary>字形(Regular/Bold/Italic/BoldItalic)。</summary>
    public string FontStyle { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultFontStyle;

    /// <summary>文字颜色(#RRGGBB)。</summary>
    public string TextColor { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultTextColor;

    /// <summary>气泡底色(#RRGGBB)。</summary>
    public string FillColor { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultFillColor;

    /// <summary>边框颜色(#RRGGBB)。</summary>
    public string BorderColor { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultBorderColor;

    /// <summary>边框粗细(px,0–10;0 = 无边框)。</summary>
    public double BorderWidth { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultBorderWidth;

    /// <summary>圆角半径(px,0–40;0 = 直角)。</summary>
    public int CornerRadius { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultCornerRadius;

    /// <summary>尾巴长度(px,0–40;0 = 无尾巴的圆角卡片)。</summary>
    public int TailLength { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultTailLength;

    /// <summary>气泡体最大宽(px,160–800;超过则换行并出现滚动)。</summary>
    public int MaxWidth { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultMaxWidth;

    /// <summary>整体不透明度(%,30–100;淡入淡出以它为目标值)。</summary>
    public int OpacityPercent { get; set; } = Plugins.ChatBubble.BubbleStyle.DefaultOpacityPercent;
}

/// <summary>
/// 应用配置:全局注册表 + 每个 Agent 独立目录。
/// 全局文件(根目录 setting.json)只记录激活 Agent 与排列顺序;
/// 每个 Agent 的全部设定持久化在 agents/{Id}/setting.json,会话记录在 agents/{Id}/sessions/,
/// 互不影响;删除 Agent 即删除其整个文件夹。
/// </summary>
public sealed class UiConfig
{
    public string ActiveAgentId { get; set; } = AgentConfig.NewId();

    /// <summary>Agent 排列顺序(托盘菜单/管理列表按此显示);实际档案以 agents/ 目录扫描为准。</summary>
    public List<string> AgentIds { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public List<AgentConfig> Agents { get; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public AgentConfig ActiveAgent =>
        Agents.FirstOrDefault(a => a.Id == ActiveAgentId) ?? Agents.FirstOrDefault()
        ?? new AgentConfig { Id = ActiveAgentId, Name = "APet" };

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // 中文等非 ASCII 字符按原样写出(默认会把所有非 ASCII 转成 \uXXXX,配置文件没法看)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>全局注册表文件(仅 ActiveAgentId + AgentIds;各 Agent 设定在各自目录)。</summary>
    private static string ConfigPath => Path.Combine(AppPaths.DataRoot, "setting.json");

    /// <summary>各 Agent 设定与数据的根目录。</summary>
    private static string AgentsRoot => AppPaths.AgentsRoot;

    private static string SafeDir(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
    }

    private static string AgentSettingPath(string id) =>
        Path.Combine(AgentsRoot, SafeDir(id), "setting.json");

    public static UiConfig Load()
    {
        var config = new UiConfig();

        // 全局注册表:ActiveAgentId + AgentIds
        try
        {
            if (File.Exists(ConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
                if (doc.RootElement.TryGetProperty("ActiveAgentId", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    config.ActiveAgentId = idEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("AgentIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array)
                    config.AgentIds = idsEl.EnumerateArray()
                        .Where(i => i.ValueKind == JsonValueKind.String)
                        .Select(i => i.GetString()!)
                        .Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[UiConfig] load failed: " + ex.Message);
        }

        // 扫描各 Agent 目录(agents/{Id}/setting.json)
        try
        {
            if (Directory.Exists(AgentsRoot))
                foreach (var dir in Directory.EnumerateDirectories(AgentsRoot))
                {
                    var path = Path.Combine(dir, "setting.json");
                    if (!File.Exists(path))
                        continue;
                    try
                    {
                        var agent = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), s_json);
                        if (agent is { Id.Length: > 0 })
                        {
                            agent.EnsureInitialized(); // 旧配置的子对象可能为 null
                            config.Agents.Add(agent);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Global.Error("[UiConfig] agent load failed: " + ex.Message);
                    }
                }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[UiConfig] agents scan failed: " + ex.Message);
        }

        // 按 AgentIds 顺序排列;未列入的(如手工放入的文件夹)按 Id 附加,保持显示稳定
        var ordered = config.AgentIds
            .Select(id => config.Agents.FirstOrDefault(a => a.Id == id))
            .OfType<AgentConfig>().ToList();
        foreach (var extra in config.Agents.Where(a => !ordered.Contains(a)).OrderBy(a => a.Id, StringComparer.Ordinal))
            ordered.Add(extra);
        config.Agents.Clear();
        foreach (var a in ordered)
            config.Agents.Add(a);

        if (config.Agents.Count == 0)
        {
            var agent = new AgentConfig();
            config.Agents.Add(agent);
            config.ActiveAgentId = agent.Id;
            SaveAgent(agent);
        }
        if (!config.Agents.Any(a => a.Id == config.ActiveAgentId))
            config.ActiveAgentId = config.Agents[0].Id;
        return config;
    }

    /// <summary>保存全局注册表(激活 Agent + 顺序)。Agent 自身设定的改动请调用该 Agent 的 Save()。</summary>
    public void Save()
    {
        AgentIds = Agents.Select(a => a.Id).ToList();
        WriteAtomically(ConfigPath, JsonSerializer.Serialize(this, s_json), "[UiConfig] save failed");
    }

    /// <summary>把单个 Agent 的设定写入它自己的目录(agents/{Id}/setting.json)。</summary>
    public static void SaveAgent(AgentConfig agent)
    {
        WriteAtomically(AgentSettingPath(agent.Id), JsonSerializer.Serialize(agent, s_json), "[UiConfig] agent save failed");
    }

    /// <summary>原子写配置:临时文件 + File.Replace。此前直接覆盖写目标文件,
    /// 写盘中断(断电/强杀)会留下半截 JSON——整个 Agent 的设定随文件损坏消失。</summary>
    private static void WriteAtomically(string path, string json, string errorPrefix)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Log.Global.Error(errorPrefix + ": " + ex.Message);
        }
    }

    /// <summary>把 SecurityConfig 构建为框架安全策略(白名单载体;档位在会话运行时按会话设定)。</summary>
    public static AgentSecurityPolicy BuildSecurity(SecurityConfig cfg)
    {
        var policy = new AgentSecurityPolicy();
        foreach (var dir in cfg.Whitelist)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                policy.AddWhitelistedDirectory(dir.Trim());
        }
        return policy;
    }
}
