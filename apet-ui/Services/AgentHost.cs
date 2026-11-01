using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using APet.Framework.Agent;
using APet.Framework.Events;
using APet.Framework.LLM;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;
using APet.Framework.Tools.BuiltIn;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using APet.Plugins.BackgroundRun;
using APet.UI.Web;
using APet.Plugins.BiliLive;
using APet.Plugins.ContextCompression;
using APet.Plugins.EmotionTags;
using APet.Plugins.EnvContext;
using APet.Plugins.Language;
using APet.Plugins.Scene;
using APet.Plugins.ChatBubble;
using APet.Plugins.DesktopPet;
using APet.Plugins.Memory;
using APet.Plugins.Mcp;
using APet.Plugins.Vision;
using APet.Plugins.Skills;
using APet.Plugins.SubAgent;
using APet.Plugins.Tts;
using APet.Plugins.Todo;
using APet.UI.ViewModels;
using APet.UI.Views;
using APet.UI.Web;
using AgentClass = APet.Framework.Agent.Agent;

namespace APet.UI.Services;

// ───────── 宿主:组装 Agent + 插件 ─────────

/// <summary>
/// UI 宿主:按 AgentConfig 组装 Agent(内置工具 + 上下文压缩 + 会话环境/TTS/BiliLive 插件),
/// 处理 TTS 音频播放与直播间事件触发的运行请求。
/// Options 保留引用:设置页失焦保存后调用 ApplyAgent 即可热更新运行参数
/// (LLM 连接信息对新创建的会话生效)。
/// </summary>
public sealed class AgentHost
{
    private readonly TtsAudioPlayer _player; // 常驻 WASAPI 串行播放(见 TtsAudioPlayer)


    private ContextCompressionPlugin? _contextCompression;
    private LanguagePlugin _language = null!;

    public AgentOptions Options { get; }
    public Agent Agent { get; }

    /// <summary>本宿主的持久化配置(与设置页共享同一实例,改动即时可读,如播放音量)。</summary>
    public AgentConfig Config { get; }

    /// <summary>环境信息插件:工作目录(主 VM 按会话提供)+ 系统/日期/时区(插件自取),拼进 system prompt。</summary>
    public EnvContextPlugin EnvContext { get; }
    public ContextCompressionPlugin ContextCompression => _contextCompression!;

    /// <summary>情景插件:$scene 指令注入/清空会话情景,随会话持久化并拼进 system prompt。</summary>
    public ScenePlugin Scene { get; }

    /// <summary>Agent 语言插件(回复语言注入 system prompt;TTS language 由宿主从同一设定推送)。</summary>
    public LanguagePlugin Language => _language;
    public TtsClientPlugin Tts { get; }
    public BiliLivePlugin BiliLive { get; }

    /// <summary>记忆插件:单 Agent 长期记忆(SQLite,全部会话共享);mem_* 工具注册在 Agent.Tools。</summary>
    public MemoryPlugin Memory { get; }

    /// <summary>任务清单插件:todo_* 工具维护多步任务计划,清单在侧边栏实时展示(全部会话共享)。</summary>
    public TodoPlugin Todo { get; }

    /// <summary>后台运行插件:bg_* 工具把耗时命令放到后台,任务列表在侧边栏实时展示。</summary>
    public BackgroundRunPlugin Background { get; }

    /// <summary>桌宠显示插件(可能未注册:引擎库仅在 Windows 可用)。</summary>
    public DesktopPetPlugin? DesktopPet { get; private set; }

    /// <summary>MCP 客户端插件:连接配置的 stdio 服务器,工具以 mcp_{服务器}_{工具} 代理注册。</summary>
    public McpPlugin? Mcp { get; private set; }

    /// <summary>技能插件:skills 目录索引注入 + skill 工具(模型按需加载技能全文)。</summary>
    public SkillsPlugin? Skills { get; private set; }

    /// <summary>子代理插件:sub_* 工具把自包含子任务委派给预配置子代理(独立 Agent+会话,侧边栏"智能体"页签观测)。</summary>
    public SubAgentPlugin SubAgent { get; }

    /// <summary>对话气泡插件:锚定在宠物正上方,流式显示助手文本;与桌宠同生命周期。</summary>
    public ChatBubblePlugin? ChatBubble { get; private set; }

    /// <summary>情绪 tag 括号风格(取自桌宠插件;未接线时按方括号)——UI 侧流式剥离据此与 TTS/气泡保持同一 pattern。</summary>
    public PetTagStyle EmotionTagStyle => DesktopPet?.TagStyle ?? PetTagStyle.SquareBrackets;

    /// <summary>桌宠点击"打开聊天窗口":UI 层订阅,激活主窗体。</summary>
    public event Action? PetOpenChatRequested;

    private PluginRegistration? _petRegistration;
    private PluginRegistration? _bubbleRegistration;

    /// <summary>桌宠插件是否启用(以注册项为准:启动配置与设置页启停都经它生效)。</summary>
    public bool IsPetEnabled => _petRegistration?.Enabled ?? false;

    /// <summary>是否存在正在运行的会话(宿主注入):桌宠启停在并发期间被拒绝,避免多轮次争夺角色气泡/TTS。</summary>
    public Func<bool>? HasRunningSessions { get; set; }

    /// <summary>启用/禁用桌宠插件及其对话气泡(驱动 Initialize/Dispose,即悬浮窗的创建与销毁)。
    /// 有会话正在运行时拒绝启停(返回 false),此时切插件会让并发轮次争夺气泡与 TTS。</summary>
    public bool SetPetEnabled(bool enabled)
    {
        if (HasRunningSessions?.Invoke() == true)
            return false;
        _petRegistration?.SetEnabled(enabled);
        _bubbleRegistration?.SetEnabled(enabled);
        return true;
    }

    /// <summary>直播间有事件待处理,宿主(主 VM)应触发一轮 RunAsync(TriggerInput)。</summary>
    public event Action? RunRequested;

    /// <summary>正在运行轮次的安全策略(sessionId → 策略,主 VM 在 RunTurnAsync 登记/清除);
    /// 子代理经 PolicyResolver 按当前会话取用,继承主对话的工作目录/权限/信任目录。</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, AgentSecurityPolicy> ActivePolicies { get; } =
        new(StringComparer.Ordinal);

    /// <summary>子代理工具需鉴权时触发(卡片投到主会话总线 + 主窗弹窗,与主对话鉴权同一通路)。</summary>
    public event Action<AuthRequestViewModel>? SubAgentAuthPopup;

    /// <summary>子代理 LLM 连接解析(视图模型注入;参数 = 主会话 Id, 子代理供应商, 子代理模型, 子代理思考等级;
    /// 空 = 继承主会话所选;未注入/解析不到时回退 Agent 默认连接)。</summary>
    public Func<string?, string?, string?, string?, ILlmClient?>? SubAgentLlmFactory { get; set; }

    /// <summary>Agent 配置保存后触发;宿主应把新 LLM 连接热更新到所有已有会话。</summary>
    public event Action? LlmConfigApplied;

    /// <summary>按 Agent 当前默认配置创建连接(会话级选择见带参重载)。</summary>
    public ILlmClient CreateLlmClient() => new OpenAiCompatibleClient(Options.Llm);

    /// <summary>按指定供应商/模型/思考等级创建会话专用连接(会话级模型选择:每个会话可用不同模型)。</summary>
    public ILlmClient CreateLlmClient(ProviderConfig provider, string model, string? thinkingEffort)
    {
        var options = new LlmClientOptions
        {
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            Model = model,
            Provider = provider.Name, // 供应商名随连接携带:助手消息按发起请求时的连接盖模型归因
            ReasoningEffort = ProviderConfig.NormalizeReasoningEffort(
                string.IsNullOrWhiteSpace(thinkingEffort) ? provider.ReasoningEffort : thinkingEffort),
            MaxTokens = provider.FindModel(model)?.MaxTokens ?? 0,
            SendThinkingField = provider.SendThinkingField,
            ThinkingStyle = provider.EffectiveThinkingField,
            Log = Options.Llm.Log,
        };
        return new OpenAiCompatibleClient(options);
    }

    /// <summary>框架要求 BaseUrl 非空:地址被清空时回退默认地址。</summary>
    private static string ResolveBaseUrl(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? ProviderConfig.DefaultBaseUrl : baseUrl;

    public AgentHost(AgentConfig agentConfig)
    {
        Config = agentConfig;
        var provider = agentConfig.Llm.Active; // Agent 生效供应商:新会话连接的初始种子(随后按会话自身选择重建)
        Options = new AgentOptions
        {
            Llm = new LlmClientOptions
            {
                BaseUrl = ResolveBaseUrl(provider.BaseUrl),
                ApiKey = provider.ApiKey,
                Model = provider.Model, // 供应商默认模型(实际连接按会话各自的选择重建)
                Provider = provider.Name, // 默认连接同样携带供应商名(子代理兜底连接等经它建连)
                ReasoningEffort = ProviderConfig.NormalizeReasoningEffort(provider.ReasoningEffort), // 归一化:显示 none 即真关闭
                SendThinkingField = provider.SendThinkingField, // 旧开关(ThinkingStyle 未指定时按其映射)
                ThinkingStyle = provider.EffectiveThinkingField, // 思考字段样式随供应商(端点异构,见 LlmClientOptions)
                MaxTokens = provider.FindModel(provider.Model)?.MaxTokens ?? 0, // 默认模型的单回复上限(0=不发送)
            },
            SystemPrompt = agentConfig.SystemPrompt,
            Temperature = agentConfig.Temperature,
            MaxToolIterations = agentConfig.MaxToolIterations,
            Security = UiConfig.BuildSecurity(agentConfig.Security),
        };
        Agent = new Agent(Options);

        // 文件工具不设工具级 RootDirectory:路径约束由会话安全策略(工作目录/权限/白名单)统一管理
        Agent.Tools.RegisterBuiltIns(o => ConfigureBuiltIns(o, agentConfig));

        // 记忆插件:单 Agent 长期记忆(SQLite,全部会话共享);mem_* 工具与 mute/screenshot 均须在
        // ApplyToolToggles 之前注册,持久化的工具开关才会套用到它们(并随"工具"页显示独立开关)
        Memory = new MemoryPlugin(agentConfig.Id, AppPaths.AgentsRoot);
        PluginConfigBridge.Push(Memory, null, null, agentConfig.Memory);
        Agent.Plugins.Register(Memory);
        Agent.Tools.Register(new MemRecordTool(Memory));
        Agent.Tools.Register(new MemRecentTool(Memory));
        Agent.Tools.Register(new MemRangeTool(Memory));
        Agent.Tools.Register(new MemUseTool(Memory));
        Agent.Tools.Register(new MemEditTool(Memory));
        Agent.Tools.Register(new MemSearchTool(Memory));
        // 记忆行为指引注入的闸门:全部 mem_* 工具被禁用时不再注入(避免模型调用不存在的工具)
        Memory.ToolsAvailable = () => Agent.Tools.GetRegisteredTools()
            .Any(t => t.Enabled && t.Definition.Name.StartsWith("mem_", StringComparison.Ordinal));

        // 任务清单插件:todo_* 工具 + 侧边栏展示(行为指引并入工具描述)
        Todo = new TodoPlugin(agentConfig.Id, AppPaths.AgentsRoot);
        Agent.Plugins.Register(Todo);
        Agent.Tools.Register(new TodoWriteTool(Todo));
        Agent.Tools.Register(new TodoReadTool(Todo));

        // 后台运行插件:bg_* 工具 + 侧边栏展示(行为指引并入工具描述)
        // Shell 与 bash 工具同源(设置页「工具 → 终端」),统一决定后台命令用哪个终端
        Background = new BackgroundRunPlugin { Shell = ResolveTerminalShell(agentConfig.TerminalShell) };
        Agent.Plugins.Register(Background);
        Agent.Tools.Register(new BgRunTool(Background));
        Agent.Tools.Register(new BgOutputTool(Background));
        Agent.Tools.Register(new BgWaitTool(Background));
        Agent.Tools.Register(new BgKillTool(Background));
        Agent.Tools.Register(new BgListTool(Background));

        _contextCompression = new ContextCompressionPlugin
        {
            AutoCompress = agentConfig.AutoCompress,
            CompressThreshold = agentConfig.CompressThreshold,
            Strategy = agentConfig.CompressStrategy,
            KeepPercent = agentConfig.CompressKeepPercent,
            SummaryMaxPercent = agentConfig.CompressSummaryMaxPercent,
            Template = ContextCompressionPlugin.ResolveTemplate(agentConfig.CompressPreset, agentConfig.CompressTemplate),
        };
        Agent.Plugins.Register(_contextCompression);

        EnvContext = new EnvContextPlugin(); // 工作目录由主 VM 按会话提供;系统/日期/时区插件自取
        Agent.Plugins.Register(EnvContext);

        Scene = new ScenePlugin();
        Agent.Plugins.Register(Scene); // 情景插件:情景文本由主 VM 按会话提供

        _language = new LanguagePlugin { Language = agentConfig.Language };
        Agent.Plugins.Register(_language);

        Tts = new TtsClientPlugin();
        Agent.Plugins.Register(Tts, enabled: false);
        PushTtsSettings(agentConfig); // TTS 设置经反射桥推送(构造与设置页保存共用,见 ApplyTtsSettings)
        _player = new TtsAudioPlayer(
            () => Config.Tts.Common.Volume,
            () => Tts.TrackSpeechEnd()); // 播放结束归还计数(队列空且无在播段时触发 SpeechDrained)
        Tts.AudioReady += (_, e) => _player.Play(e);
        Tts.SpeechDrained += OnTtsDrained; // 全部朗读完毕 → 通知气泡启动隐藏倒计时
        // 同步把进度推给 Web:聊天页(发起会话)弹模态进度框,网页侧也能看到预热进度
        Tts.WarmupStarted += total =>
        {
            if (Tts.WarmupSessionId is { } sid)
                WebEventHub.Publish(Config.Id, sid, "warmup_started", new { total });
        };
        Tts.WarmupProgress += (done, total, current) =>
        {
            if (Tts.WarmupSessionId is { } sid)
                WebEventHub.Publish(Config.Id, sid, "warmup_progress", new { done, total, current });
        };
        Tts.WarmupFinished += message =>
        {
            if (Tts.WarmupSessionId is { } sid)
                WebEventHub.Publish(Config.Id, sid, "warmup_finished", new { message });
        };
        Tts.SpeakFailed += (_, text) => Log.Global.Error($"[TtsClient] failed to synthesize: {text[..Math.Min(50, text.Length)]}…");
        Agent.Plugins.Find(Tts.Name)?.SetEnabled(agentConfig.Tts.Common.Enabled);

        BiliLive = new BiliLivePlugin();
        var biliReg = Agent.Plugins.Register(BiliLive, enabled: false);
        // BiliLive 设置经反射桥自动推送;配置节未覆盖的插件设置(thankGift/价格阈值等,
        // 设置页暂无入口)记 Debug 日志并沿用插件默认值
        PluginConfigBridge.Push(BiliLive, null, null, agentConfig.BiliLive);
        // 与 ApplyBiliSettings 同语义:总开关关闭时即使配置了房间号也不建连
        if (agentConfig.BiliLive.Enabled && !string.IsNullOrWhiteSpace(agentConfig.BiliLive.RoomCode))
            biliReg.SetEnabled(true); // Initialize: 建连(未配置则保持禁用)
        BiliLive.RunRequested += () => RunRequested?.Invoke();
        // $blive 绑定持久化:绑定/解绑即镜像进 Agent 设定并落盘,重启后在上方恢复;
        // 指向已不存在会话的无效绑定由主 VM 载入会话清单后校验清理
        BiliLive.BoundSessionChanged += () =>
        {
            agentConfig.BiliLive.BoundSessionId = BiliLive.BoundSessionId ?? "";
            agentConfig.Save();
        };
        BiliLive.BindSession(string.IsNullOrWhiteSpace(agentConfig.BiliLive.BoundSessionId)
            ? null
            : agentConfig.BiliLive.BoundSessionId);

        // mute():BiliLive 的静默工具——仅在直播间模式启用时暴露给模型;调用由插件在
        // OnAssistantMessage 拦截(移除调用并清空正文 → 本轮静默),ExecuteAsync 只是兜底
        _muteTool = new MuteTool(() => BiliLive.Enabled);
        Agent.Tools.Register(_muteTool);

        // MCP 服务器:stdio 连接,服务器工具以 mcp_{服务器}_{工具} 代理注册;
        // 启停/清单由设置页驱动(保存后按差异断开/重连),详见 ApplyMcpSettings
        Mcp = new McpPlugin { Tools = Agent.Tools };
        Agent.Plugins.Register(Mcp, enabled: false);
        ApplyMcpSettings(agentConfig.Mcp);

        // 技能:技能固定存于用户目录 .apet\skills;索引注入 system prompt + skill 工具 + $skills 指令
        Skills = new SkillsPlugin();
        Agent.Plugins.Register(Skills, enabled: false);
        ApplySkillsSettings(agentConfig.Skills);

        // 子代理:sub_* 工具把子任务委派给预配置子代理(独立 Agent + 会话,仅挂压缩插件,见视图模型注入的工厂);
        // 定义清单/总开关由设置页驱动(保存后按 ApplySubAgentSettings 热应用,见下方 ApplyToolToggles 之后)
        SubAgent = new SubAgentPlugin();
        Agent.Plugins.Register(SubAgent, enabled: false);
        Agent.Tools.Register(new SubRunTool(SubAgent));
        Agent.Tools.Register(new SubWaitTool(SubAgent));
        Agent.Tools.Register(new SubTellTool(SubAgent));
        Agent.Tools.Register(new SubListTool(SubAgent));
        Agent.Tools.Register(new SubKillTool(SubAgent));
        // 行为指引注入闸门:全部 sub_* 工具被禁用时不再注入(避免模型调用不存在的工具)
        SubAgent.ToolsAvailable = () => Agent.Tools.GetRegisteredTools()
            .Any(t => t.Enabled && t.Definition.Name.StartsWith("sub_", StringComparison.Ordinal));
        // 宿主注入:LLM 连接按主会话的供应商/模型解析(会话级选择由视图模型注入 SubAgentLlmFactory;
        // 未注入/解析不到时回退 Agent 默认连接);内置工具配置与主 Agent 同源(终端 shell/代理);
        // 事件帧转发 Web 事件总线(侧边栏/模态框经 SSE 消费);策略按当前会话解析
        SubAgent.LlmClientFactory = (sessionId, provider, model, effort) =>
            SubAgentLlmFactory?.Invoke(sessionId, provider, model, effort) ?? CreateLlmClient();
        SubAgent.ToolConfig = o => ConfigureBuiltIns(o, agentConfig);
        SubAgent.DisplayNames = name => Agent.Tools.GetRegisteredTools()
            .FirstOrDefault(t => t.Definition.Name == name)?.Definition.GetDisplayName() ?? name;
        // 子代理工具帧按主聊天同一套展示格式化(ToolCallPresenter):模态框工具行与主窗体观感一致
        SubAgent.ToolFrameFormatter = (name, display, args, resultText, rawError, workdir) =>
        {
            var row = ToolCallPresenter.Create(display, name, args ?? "", workdir);
            if (resultText is not null)
                ToolCallPresenter.ApplyResult(row, resultText);
            return new
            {
                toolName = name,
                displayName = display,
                summary = row.Summary,
                detail = row.DetailBody,
                hasDetail = row.HasDetail,
                pathLink = row.PathLink,
                statAdd = row.StatAdd,
                statDel = row.StatDel,
                diffLines = row.DiffLines.Select(d => new { lineNo = d.LineNo, text = d.Text, kind = d.Kind }).ToList(),
                resultSummary = row.ResultSummary,
                isError = row.IsError || rawError,
            };
        };
        SubAgent.EventSink = (runId, type, data) => Web.WebEventHub.Publish(Config.Id, runId, type, data);
        SubAgent.PolicyResolver = sessionId =>
            sessionId is { Length: > 0 } id && ActivePolicies.TryGetValue(id, out var p) ? p : null;
        SubAgent.AuthHandler = (ctx, ct) => HandleSubAgentAuthAsync(ctx, ct);

        // screenshot():桌面截图,以图片回传给多模态 LLM(主屏/第 N 屏)
        Agent.Tools.Register(new ScreenshotTool());

        // view_image():读取磁盘上的图片文件回传给多模态 LLM(视觉组,与截图同组)
        Agent.Tools.Register(new ViewImageTool());

        // ask():对不明确的决定向用户发起结构化询问(面板盖住聊天输入框,支持键盘操作);
        // 挂起-应答与鉴权审批同款机制:WebEventHub 登记 + SSE 广播 + REST 应答,详见 AskUserAsync
        Agent.Tools.Register(new AskUserTool(AskUserAsync));

        // 外部插件(plugins 目录 DLL):每 Agent 一份独立实例,启停与设置来自持久化袋;
        // 注册在 ApplyToolToggles 之前,持久化的工具开关同样能套用到外部插件的工具
        RegisterExternalPlugins(agentConfig);

        // 全部工具注册完毕后再套用持久化的开关状态(未记录的工具默认启用)
        ApplyToolToggles(agentConfig);
        // 内置插件总开关(设置页「内置插件」卡片开关;MCP/技能在各自注册处应用)
        ApplyMemorySettings(agentConfig.Memory);
        ApplyBiliSettings(agentConfig.BiliLive);
        ApplySceneSettings(agentConfig.SceneEnabled);
        ApplySubAgentSettings(agentConfig.SubAgents); // 必须在 ApplyToolToggles 之后:插件总开关联动 sub_* 工具开关

        // 桌宠显示插件:渲染引擎在 Windows 可用;设置推送与 TTS/BiliLive 同构
        try
        {
            DesktopPet = new DesktopPetPlugin();
            _petRegistration = Agent.Plugins.Register(DesktopPet, enabled: false);
            ApplyPetSettings(agentConfig.DesktopPet);
            // 启用前先按持久化坐标落位,避免窗口从默认坐标闪现再跳到上次位置
            DesktopPet.SetWindowPosition(agentConfig.DesktopPet.PosX, agentConfig.DesktopPet.PosY);
            if (agentConfig.DesktopPet.Enabled)
                _petRegistration.SetEnabled(true);
            DesktopPet.OpenChatRequested += () => PetOpenChatRequested?.Invoke();
            // TTS 的 tag 括号风格由桌宠决定(解析与注入同源);情绪联动闸门跟随桌宠启停
            Tts.TagStyleProvider = () => DesktopPet.TagStyle;
            Tts.EmotionSyncGate = () => DesktopPet is { Enabled: true };
            Tts.EmotionSyncGateDetail = () => DesktopPet is null ? "DesktopPet=null" : $"DesktopPet.Enabled={DesktopPet.Enabled}";
            // TTS 解析到情感 tag → 驱动桌宠切表情(与发声同步;TTS 未启用时不触发)
            Tts.EmotionTag += tag => DesktopPet?.TriggerEmotion(tag);

            // 对话气泡:锚定在宠物正上方,流式显示助手文本;与桌宠同启停
            // (DesktopPet 实现 IPetAnchor;朗读状态经委托注入,气泡不感知 TTS 具体类型)
            ChatBubble = new ChatBubblePlugin(DesktopPet);
            ChatBubble.IsSpeechActive = () => Tts is { Enabled: true } && Tts.HasActiveSpeech;
            // 点气泡同样打开聊天窗口(长回复看全文),复用宠物点击的宿主事件
            ChatBubble.OpenChatRequested += () => PetOpenChatRequested?.Invoke();
            _bubbleRegistration = Agent.Plugins.Register(ChatBubble, enabled: false);
            ApplyChatBubbleSettings(agentConfig.ChatBubble);
            if (agentConfig.DesktopPet.Enabled)
                _bubbleRegistration.SetEnabled(true);

            // 悬浮窗标题带 Agent 名(供 OBS 按窗口标题识别):桌宠窗 "APet - 名(引擎)",气泡窗 "APet - 名(对话气泡)"
            if (!string.IsNullOrWhiteSpace(agentConfig.Name))
            {
                DesktopPet.SetAgentName(agentConfig.Name);
                ChatBubble.SetAgentName(agentConfig.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Global.Error("[DesktopPet] registration failed: " + ex.Message);
            DesktopPet = null;
            ChatBubble = null;
        }

        // 宿主配置视图:全部插件注册完毕后,把 [PluginVisible] 标注的 AgentConfig 属性
        // 以只读视图注入每个插件(值活读,设置页保存后插件自动读到新值)
        _configView = new PluginConfigView(agentConfig);
        foreach (var registration in Agent.Plugins.All)
            if (registration.Plugin is AgentPluginBase basePlugin)
                basePlugin.AgentConfigView = _configView;
    }

    /// <summary>
    /// 把 TTS 配置推送到插件(构造与设置页保存共用):经反射桥从四个引擎子节自动推送。
    /// 插件的 GSV/Server 设置名带引擎前缀而配置属性没有(gsvVoice ↔ Gsv.Voice 等),必须经别名声明;
    /// gsvVoices 是 List<GsvVoiceSetting>,桥接时序列化为 JSON 字符串。
    /// </summary>
    private void PushTtsSettings(AgentConfig cfg)
    {
        PluginConfigBridge.Push(Tts,
            new Dictionary<string, Func<object, object?>>
            {
                ["gsvVoices"] = v => System.Text.Json.JsonSerializer.Serialize(v),
            },
            new Dictionary<string, string>
            {
                // 限定符 = 配置节类型名(GetProperty 按 节类型/属性 定位)
                ["serverVoice"] = "TtsServerConfig/Voice",
                ["gsvVoice"] = "TtsGsvConfig/Voice",
                ["gsvModelDir"] = "TtsGsvConfig/ModelDir",
                ["gsvDevice"] = "TtsGsvConfig/Device",
                ["gsvCutMute"] = "TtsGsvConfig/CutMute",
                ["gsvCutMinlen"] = "TtsGsvConfig/CutMinlen",
                ["gsvVoices"] = "TtsGsvConfig/Voices",
            },
            cfg.Tts.Common, cfg.Tts.Server, cfg.Tts.Windows, cfg.Tts.Gsv);
        // 朗读语言与 Agent 回复语言同源(设置页"语言"组合框),不在 Tts 配置节内,单独推送
        Tts.TrySetSetting("language", cfg.Language, out _);
    }

    /// <summary>TTS 设置热更:保存即生效(值推送 + 总开关经插件注册对象驱动 Initialize/Dispose)。</summary>
    public void ApplyTtsSettings(AgentConfig cfg)
    {
        PushTtsSettings(cfg);
        Agent.Plugins.Find(Tts.Name)?.SetEnabled(cfg.Tts.Common.Enabled);
    }

    /// <summary>
    /// 把 MCP 配置推送到插件(构造与设置页保存共用):总开关关闭或无可用服务器 → 停用插件(断开全部);
    /// 否则按清单差异断开/重连。连接为后台进行,失败记日志,不影响 Agent 运行。
    /// </summary>
    public void ApplyMcpSettings(McpConfig cfg)
    {
        if (Mcp is null)
            return;
        var registration = Agent.Plugins.Find(Mcp.Name);
        if (registration is null)
            return;
        var shouldEnable = cfg.Enabled && cfg.Servers.Any(s => s.Enabled
            && (!string.IsNullOrWhiteSpace(s.Command) || !string.IsNullOrWhiteSpace(s.Url)));
        registration.SetEnabled(shouldEnable);
        if (shouldEnable)
        {
            var specs = cfg.Servers
                .Where(s => s.Enabled && (!string.IsNullOrWhiteSpace(s.Command) || !string.IsNullOrWhiteSpace(s.Url)))
                .Select(s => new McpPlugin.ServerSpec(
                    string.IsNullOrWhiteSpace(s.Name)
                        ? (string.IsNullOrWhiteSpace(s.Command) ? s.Url.Trim() : s.Command.Trim())
                        : s.Name.Trim(),
                    s.Type, s.Command.Trim(), s.Arguments, s.Url.Trim(), s.EnvJson, s.HeadersJson,
                    s.WorkingDirectory, s.TimeoutMs, s.ProtocolVersion, s.TrustTools, s.Enabled))
                .ToList();
            _ = Mcp.ApplyServersAsync(specs);
        }
    }

    /// <summary>把技能总开关与停用清单推送到插件(构造与设置页保存共用);技能固定存于用户目录 .apet\skills。</summary>
    public void ApplySkillsSettings(SkillsConfig cfg)
    {
        if (Skills is null)
            return;
        Agent.Plugins.Find(Skills.Name)?.SetEnabled(cfg.Enabled);
        Skills.SetDisabledSkills(cfg.DisabledSkills);
    }

    /// <summary>
    /// 把子代理设定推送到插件(构造与设置页保存共用):总开关关闭或无有效定义 → 停用插件并下线 sub_* 工具;
    /// 否则热替换定义清单(在跑的子代理不受影响,新 sub_run 用新定义)。
    /// 须在 ApplyToolToggles 之后调用:插件总开关联动 sub_* 工具开关,恢复各自持久化开关。
    /// </summary>
    public void ApplySubAgentSettings(SubAgentConfig cfg)
    {
        if (SubAgent is null)
            return;
        var defs = (cfg.Agents ?? new List<SubAgentDefConfig>())
            .Where(a => a.Enabled && !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new SubAgentPlugin.Definition(
                a.Name.Trim(), a.SystemPrompt ?? "", a.Tools ?? new List<string>(),
                a.Provider ?? "", a.Model ?? "", a.ThinkingEffort ?? "", a.WorkingDirectoryAccess ?? "ReadOnly",
                a.MaxToolIterations))
            .ToList();
        SubAgent.SetDefinitions(defs);
        var enabled = cfg.Enabled && defs.Count > 0;
        Agent.Plugins.Find(SubAgent.Name)?.SetEnabled(enabled);
        foreach (var tool in Agent.Tools.GetRegisteredTools()
                     .Where(t => t.Definition.Name.StartsWith("sub_", StringComparison.Ordinal)))
        {
            var persisted = Config.ToolEnabled.TryGetValue(tool.Definition.Name, out var on) ? on : true;
            tool.Enabled = enabled && persisted;
        }
    }

    /// <summary>
    /// 子代理工具鉴权(宿主委托):卡片投到主会话总线(用户在主聊天窗经 REST 应答),
    /// 主窗弹窗与主对话鉴权同一通路;运行取消视为拒绝。
    /// </summary>
    private async Task<AuthorizationResult> HandleSubAgentAuthAsync(SubAgentAuthContext ctx, CancellationToken cancellationToken)
    {
        var displayName = Agent.Tools.GetRegisteredTools()
            .FirstOrDefault(t => t.Definition.Name == ctx.ToolCall.Name)?.Definition.GetDisplayName() ?? ctx.ToolCall.Name;
        var verb = ctx.Effect.Access switch
        {
            FileAccessKind.Write => "请求写入",
            FileAccessKind.Read => "请求读取",
            _ => "请求执行",
        };
        var sb = new System.Text.StringBuilder($"子代理 {ctx.RunName} {displayName} {verb}");
        if (!string.IsNullOrWhiteSpace(ctx.ToolCall.ArgumentsJson))
            sb.Append('\n').Append(ToolCallPresenter.DescribeArguments(ctx.ToolCall.Name, ctx.ToolCall.ArgumentsJson));
        if (ctx.Effect.Paths.Count > 0)
            sb.Append("\n范围: ").Append(string.Join("  ", ctx.Effect.Paths));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var card = new AuthRequestViewModel(sb.ToString(), tcs);
        if (!string.IsNullOrEmpty(ctx.MainSessionId))
            Web.WebEventHub.RegisterAuth(Config.Id, ctx.MainSessionId, card); // Web 形态:浏览器经 REST 给出结论
        SubAgentAuthPopup?.Invoke(card); // 主窗弹窗(与主对话鉴权弹窗同通路)
        using (cancellationToken.Register(() =>
        {
            tcs.TrySetResult(false);
            card.Resolve(false);
        }))
        {
            var granted = await tcs.Task;
            return granted ? AuthorizationResult.Allow : AuthorizationResult.Deny("denied by user");
        }
    }

    /// <summary>mute 工具实例(启停跟随直播插件,见 ApplyBiliSettings)。</summary>
    private MuteTool? _muteTool;

    /// <summary>
    /// 记忆插件总开关:关闭即停用召回注入并禁用全部 mem_* 工具;开启时恢复各工具的持久化开关
    /// (未记录过的工具默认启用)。
    /// </summary>
    public void ApplyMemorySettings(MemoryConfig cfg)
    {
        Agent.Plugins.Find(Memory.Name)?.SetEnabled(cfg.Enabled);
        foreach (var tool in Agent.Tools.GetRegisteredTools()
                     .Where(t => t.Definition.Name.StartsWith("mem_", StringComparison.Ordinal)))
        {
            var persisted = Config.ToolEnabled.TryGetValue(tool.Definition.Name, out var on) ? on : true;
            tool.Enabled = cfg.Enabled && persisted;
        }
    }

    /// <summary>
    /// 直播互动总开关:关闭即断开直播间连接并禁用 mute 工具;开启时按持久化开关恢复
    /// (BiliLive.Enabled 随插件启停,直播间模式的运行闸门依赖它)。
    /// </summary>
    public void ApplyBiliSettings(BiliLiveConfig cfg)
    {
        // 热推设置进插件实例(与启动路径的 PluginConfigBridge.Push 同构):roomCode/cookie 等经
        // TrySetSetting 写入属性并触发 OnSettingChanged → RestartConnection,连接引擎才会用新值。
        // 此前保存路径漏了这一步,引擎一直用启动时的快照(改配置后仍用旧 cookie 重连报错,重启才生效)
        PluginConfigBridge.Push(BiliLive, null, null, cfg);
        // 与启动条件同语义:总开关开 且 已配置房间号才启用(清空房间号保存 = 断开并停用)
        Agent.Plugins.Find(BiliLive.Name)?.SetEnabled(cfg.Enabled && !string.IsNullOrWhiteSpace(cfg.RoomCode));
        if (_muteTool is { } mute)
        {
            var persisted = Config.ToolEnabled.TryGetValue(mute.Definition.Name, out var on) ? on : true;
            mute.Enabled = cfg.Enabled && persisted;
        }
    }

    /// <summary>
    /// 该工具是否应出现在设置页工具清单:归属插件的工具随插件启停隐藏/恢复
    /// (记忆的 mem_*、直播的 mute 与 bili_live_events;其余恒可见)。
    /// </summary>
    public bool IsToolVisibleInSettings(string toolName)
    {
        if (toolName.StartsWith("mem_", StringComparison.Ordinal))
            return Agent.Plugins.Find(Memory.Name)?.Enabled ?? true;
        if (string.Equals(toolName, "mute", StringComparison.Ordinal)
            || string.Equals(toolName, "bili_live_events", StringComparison.Ordinal))
            return Agent.Plugins.Find(BiliLive.Name)?.Enabled ?? true;
        if (toolName.StartsWith("sub_", StringComparison.Ordinal))
            return SubAgent?.Enabled ?? true;
        return true;
    }

    /// <summary>
    /// ask 工具的宿主委托:把问题登记为待决询问并广播给当前会话的前端,挂起等待用户
    /// 应答(答案 JSON 原样回传模型)或跳过/中断(返回 null,工具据此提示模型自行假设)。
    /// 会话归属取 ToolExecutionScope.SessionId(AsyncLocal),与鉴权审批的挂起-应答同构。
    /// </summary>
    private async Task<string?> AskUserAsync(IReadOnlyList<Framework.Tools.BuiltIn.AskQuestion> questions,
        CancellationToken cancellationToken)
    {
        var sessionId = ToolExecutionScope.SessionId ?? "";
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var askId = Web.WebEventHub.RegisterAsk(Config.Id, sessionId, questions, tcs);
        using (cancellationToken.Register(() => Web.WebEventHub.CancelAsk(askId)))
            return await tcs.Task;
    }

    /// <summary>情景插件总开关:关闭即停用 $scene 指令与情景注入。</summary>
    public void ApplySceneSettings(bool enabled) =>
        Agent.Plugins.Find(Scene.Name)?.SetEnabled(enabled);

    /// <summary>把持久化配置推送到桌宠插件(供构造与设置页保存共用)。模型与映射按引擎取自各自的子配置。</summary>
    public void ApplyPetSettings(DesktopPetConfig cfg)
    {
        if (DesktopPet is null)
            return;
        var model = cfg.CurrentModel;
        var spine = model is SpinePetConfig spineCfg ? spineCfg : null;
        var basic = model as BasicPetConfig;
        var vrmModel = model as VrmPetConfig;
        DesktopPet.SetSettings(new Dictionary<string, object?>
        {
            ["engine"] = cfg.Engine,
            ["modelPath"] = model.ModelPath,
            // 图集仅 Spine 有;Live2D 资源按 model3.json 的 FileReferences 自动解析
            ["atlasPath"] = spine?.AtlasPath ?? "",
            // 基础引擎的淡入淡出过渡(其余引擎忽略)
            ["fadeTransition"] = basic?.FadeTransition ?? false,
            ["fadeDurationMs"] = basic?.FadeDurationMs ?? 250,
            // VRM 引擎的渲染风格(其余引擎忽略)
            ["shadingStyle"] = vrmModel?.ShadingStyle ?? "mtoon",
            // VRM 引擎:导入的 VRMA 动作路径(情绪映射里动作名=文件基名;其余引擎忽略)
            ["vrmaPaths"] = System.Text.Json.JsonSerializer.Serialize(vrmModel?.AnimationPaths ?? new List<string>()),
            ["scale"] = cfg.Scale,
            ["idleMotions"] = System.Text.Json.JsonSerializer.Serialize(model.IdleMotions),
            ["idleIntervalMs"] = cfg.IdleIntervalSeconds * 1000,
            ["idleJitterMs"] = cfg.IdleJitterSeconds * 1000,
            // 眨眼动作仅 Spine 有;Live2D 用内建 AutoEyeBlink,blinkMotion 应留空
            ["blinkMotion"] = spine?.BlinkMotion ?? "",
            ["blinkIntervalMs"] = cfg.BlinkIntervalSeconds * 1000,
            ["blinkJitterMs"] = cfg.BlinkJitterSeconds * 1000,
            ["emotionMap"] = System.Text.Json.JsonSerializer.Serialize(model.EmotionMap),
            ["partOpacities"] = System.Text.Json.JsonSerializer.Serialize(cfg.Live2D.PartOpacities),
            // Live2D 内建眨眼/呼吸开关(Spine 引擎忽略)
            ["autoBlink"] = cfg.Live2D.AutoEyeBlink,
            ["breath"] = cfg.Live2D.BreathEnabled,
            ["tagStyle"] = cfg.TagStyle,
            ["clickAction"] = cfg.ClickAction,
            ["doubleClickAction"] = cfg.DoubleClickAction,
            ["rightClickAction"] = cfg.RightClickAction,
        });
    }

    /// <summary>把对话气泡设置推送到插件(供构造与设置页保存共用);气泡正显示时立即重新锚定/重排版。
    /// 数值越界先夹回合法范围 —— 配置可能来自旧文件,插件的 Min/Max 校验会拒绝未夹取的值;
    /// 颜色/字体/字形原样推送,由插件侧统一归一化(颜色非法保持原值、字形非法由设置校验拒绝)。</summary>
    public void ApplyChatBubbleSettings(ChatBubbleConfig cfg)
    {
        if (ChatBubble is null)
            return;
        PluginConfigBridge.Push(ChatBubble, new Dictionary<string, Func<object, object?>>
        {
            ["topOffset"] = v => Math.Clamp(Convert.ToInt32(v), -300, 300),
            ["leftOffset"] = v => Math.Clamp(Convert.ToInt32(v), -300, 300),
            ["rightOffset"] = v => Math.Clamp(Convert.ToInt32(v), -300, 300),
            ["fontSize"] = v => Math.Clamp(Convert.ToDouble(v), 6, 48),
            ["borderWidth"] = v => Math.Clamp(Convert.ToDouble(v), 0, 10),
            ["cornerRadius"] = v => Math.Clamp(Convert.ToInt32(v), 0, 40),
            ["tailLength"] = v => Math.Clamp(Convert.ToInt32(v), 0, 40),
            ["maxWidth"] = v => Math.Clamp(Convert.ToInt32(v), 160, 800),
            ["opacityPercent"] = v => Math.Clamp(Convert.ToInt32(v), 30, 100),
        }, null, cfg);
    }

    /// <summary>设置页保存后调用:压缩参数热更新到运行中的插件(上限仍由 ContextLimit 实时解析)。</summary>
    public void ApplyCompressionSettings(AgentConfig agent)
    {
        var c = _contextCompression;
        if (c is null)
            return;
        c.AutoCompress = agent.AutoCompress;
        c.CompressThreshold = agent.CompressThreshold;
        c.Strategy = agent.CompressStrategy;
        c.KeepPercent = agent.CompressKeepPercent;
        c.SummaryMaxPercent = agent.CompressSummaryMaxPercent;
        // 生效模板:内置预设取预设正文;自定义(或旧版数据)取用户模板,留空回退缺省
        c.Template = ContextCompressionPlugin.ResolveTemplate(agent.CompressPreset, agent.CompressTemplate);
    }

    /// <summary>设置页保存后调用:把 AgentConfig 热更新到运行中的 Agent(LLM 连接对新会话生效)。</summary>
    public void ApplyAgent(AgentConfig agent)
    {
        var provider = agent.Llm.Active; // Agent 生效供应商:新会话连接的种子(已有会话按各自选择重建)
        Options.SystemPrompt = agent.SystemPrompt;
        Options.Temperature = agent.Temperature;
        Options.MaxToolIterations = agent.MaxToolIterations;
        Options.Llm.BaseUrl = ResolveBaseUrl(provider.BaseUrl);
        Options.Llm.ApiKey = provider.ApiKey;
        Options.Llm.Model = provider.Model; // 供应商默认模型
        Options.Llm.MaxTokens = provider.FindModel(provider.Model)?.MaxTokens ?? 0; // 默认模型的单回复上限
        Options.Llm.ReasoningEffort = ProviderConfig.NormalizeReasoningEffort(provider.ReasoningEffort); // 归一化:显示 none 即真关闭
        Options.Llm.SendThinkingField = provider.SendThinkingField; // 旧开关(ThinkingStyle 未指定时按其映射)
        Options.Llm.ThinkingStyle = provider.EffectiveThinkingField; // 思考字段样式随供应商(端点异构)
        // web_fetch 代理与 LLM 连接保持同源,变更后重建连接
        if (Agent.Tools.GetRegisteredTools().FirstOrDefault(t => t.Definition.Name == "web_fetch")
            is WebFetchTool webFetch)
        {
            webFetch.Options.ProxyMode = agent.Llm.ProxyMode;
            webFetch.Options.ProxyUrl = agent.Llm.ProxyUrl;
            webFetch.ResetClient();
        }
        // 终端 shell 随「工具 → 终端」设定热更:bash 工具(定义/执行)与后台 bg_run 共用同一选择
        if (Agent.Tools.GetRegisteredTools().FirstOrDefault(t => t.Definition.Name == "bash")
            is TerminalTool terminal)
        {
            terminal.Options.Shell = ResolveTerminalShell(agent.TerminalShell);
        }
        Background.Shell = ResolveTerminalShell(agent.TerminalShell);
        Options.Security = UiConfig.BuildSecurity(agent.Security);
        ApplyCompressionSettings(agent); // 压缩参数随保存热更新(含保留窗口/摘要上限/模板)
        LlmConfigApplied?.Invoke(); // 连接以 Agent 为准:保存后所有已有会话热更换连接
    }

    /// <summary>
    /// 把设置页的终端取值归一化为框架/后台共用的 shell 标记:
    /// bash / cmd / powershell(空值按 powershell,与 bash 工具 Windows 默认一致);
    /// 未识别的完整路径原样透传给框架(仍按 bash 式 -c 执行)。
    /// </summary>
    private static string ResolveTerminalShell(string? shell)
    {
        var s = (shell ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "" or "powershell" or "pwsh" => "powershell",
            "cmd" or "cmd.exe" => "cmd",
            "bash" or "sh" or "git-bash" => "bash",
            _ => s.Length > 0 ? s : "powershell",
        };
    }

    /// <summary>
    /// 内置工具批量配置(主 Agent 构造与子代理共用):终端 shell 随 Agent 设定
    /// (输出编码按内容自动判定,不再固定单一编码);web_fetch 与 LLM 请求共用同一份代理设定。
    /// </summary>
    private static void ConfigureBuiltIns(BuiltInToolOptions options, AgentConfig agentConfig)
    {
        options.Terminal.TimeoutSeconds = 60;
        options.Terminal.Shell = ResolveTerminalShell(agentConfig.TerminalShell);
        options.WebFetch.ProxyMode = agentConfig.Llm.ProxyMode;
        options.WebFetch.ProxyUrl = agentConfig.Llm.ProxyUrl;
    }

    /// <summary>按持久化的开关状态应用工具启用(未记录的工具默认启用)。</summary>
    public void ApplyToolToggles(AgentConfig agentConfig)
    {
        foreach (var tool in Agent.Tools.GetRegisteredTools())
        {
            if (agentConfig.ToolEnabled.TryGetValue(tool.Definition.Name, out var enabled))
                tool.Enabled = enabled;
        }
    }

    /// <summary>
    /// 切换工具的启用状态(设置页调用):普通工具直接切换;插件工具(如 skill)的适配器
    /// Enabled 只读,开关落到所属插件(整体启停,生命周期自动驱动)。
    /// </summary>
    public void SetToolEnabled(string name, bool enabled)
    {
        var tool = Agent.Tools.GetRegisteredTools().FirstOrDefault(t => t.Definition.Name == name);
        if (tool is not null)
        {
            tool.Enabled = enabled;
            return;
        }
        FindToolPluginRegistration(name)?.SetEnabled(enabled);
    }

    /// <summary>
    /// 持久化工具的启用状态:普通工具写 ToolEnabled 表;插件工具写所属插件的总开关设定
    /// (重启后由插件设置恢复,与"插件页总开关"同一路径,避免两处状态打架)。
    /// </summary>
    public void PersistToolEnabled(string name, bool enabled)
    {
        if (FindToolPluginRegistration(name)?.Plugin is SkillsPlugin)
        {
            Config.Skills ??= new SkillsConfig();
            Config.Skills.Enabled = enabled;
            return;
        }
        Config.ToolEnabled[name] = enabled;
    }

    /// <summary>工具所属的插件注册对象(如 skill → Skills);普通工具返回 null。</summary>
    private PluginRegistration? FindToolPluginRegistration(string name) =>
        Agent.Plugins.All.FirstOrDefault(p => p.Plugin is AgentPluginBase plugin && plugin.IsTool
            && string.Equals(plugin.ToolDefinition.Name, name, StringComparison.Ordinal));

    /// <summary>释放本宿主的全部插件(断开直播连接/TTS 工作线程/桌宠悬浮窗等);Agent 被删除或禁用时调用。</summary>
    public void Dispose()
    {
        _contextCompression?.Dispose();
        Memory.Dispose(); // 关闭记忆库连接(文件保留,随 Agent 目录删除)
        Todo.Dispose();
        Background.Dispose(); // 终止仍在运行的后台任务(整棵进程树)
        Tts.Dispose();
        BiliLive.Dispose();
        _player.Reset(); // 关闭常驻播放设备
        DesktopPet?.Dispose();
        Mcp?.Dispose(); // 结束 MCP 服务器子进程并注销代理工具
        Skills?.Dispose();
        SubAgent?.Dispose(); // 终止仍在运行的子代理
    }

    /// <summary>
    /// 异步插件清理:逐个 await 插件的 DisposeAsync(无法同步完成的异步清理,如断开远程连接)。
    /// 须在 <see cref="Dispose"/> 之后调用;应用退出路径 await 本方法,
    /// Agent 删除/禁用路径后台执行(内部已做异常隔离,不会抛出)。
    /// </summary>
    public async Task DisposePluginsAsync()
    {
        foreach (var registration in Agent.Plugins.All)
        {
            if (registration.Plugin is AgentPluginBase { } plugin)
            {
                try
                {
                    await plugin.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Global.Error($"[Plugin] {registration.Name} DisposeAsync failed: {ex}");
                }
            }
        }
    }

    /// <summary>TTS 全部朗读完毕:桌宠切回待机、气泡启动隐藏倒计时(回到 UI 线程)。</summary>
    private void OnTtsDrained(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DesktopPet?.OnTtsIdle();
            ChatBubble?.OnTtsIdle();
        });

    // ───────── 外部插件(plugins 目录 DLL) ─────────

    private readonly List<AgentPluginBase> _externalPlugins = new();
    private readonly Dictionary<string, Type> _externalPluginTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>宿主配置视图:[PluginVisible] 标注的 AgentConfig 属性对全部插件开放(构造时创建,重建实例时复用)。</summary>
    private readonly PluginConfigView _configView;

    /// <summary>本 Agent 载入的外部插件实例(设置页读写它们的 [PluginSetting] 属性)。</summary>
    public IReadOnlyList<AgentPluginBase> ExternalPlugins => _externalPlugins;

    /// <summary>按 plugins 目录发现的类型实例化并注册全部外部插件,按 (LoadOrder, Name) 顺序注册
    /// (有初始化依赖的插件按声明先载入),启停与设置灌入来自持久化袋。</summary>
    private void RegisterExternalPlugins(AgentConfig agentConfig)
    {
        var instances = new List<(Type Type, AgentPluginBase Plugin)>();
        foreach (var type in ExternalPluginLoader.Discover())
        {
            try
            {
                if (Activator.CreateInstance(type) is not AgentPluginBase plugin)
                    continue;
                instances.Add((type, plugin));
            }
            catch (Exception ex)
            {
                Log.Global.Error("[ExternalPlugins] failed to instantiate " + type.Name + ": " + ex.Message);
            }
        }
        foreach (var (type, plugin) in instances.OrderBy(p => p.Plugin.LoadOrder).ThenBy(p => p.Plugin.Name, StringComparer.Ordinal))
        {
            try
            {
                agentConfig.PluginSettings.TryGetValue(plugin.Name, out var bag);
                Agent.Plugins.Register(plugin, bag?.Enabled ?? true);
                plugin.AgentId = agentConfig.Id; // Agent 级事件发布凭据(PluginEvents.PublishAgent)
                if (bag is { Values.Count: > 0 })
                {
                    // 灌历史设置:单个失败不阻断其余(袋中可能残留已不存在的设置名)
                    foreach (var kv in bag.Values)
                        plugin.TrySetSetting(kv.Key, ExternalPluginValues.FromJson(kv.Value), out _);
                }
                _externalPlugins.Add(plugin);
                _externalPluginTypes[plugin.Name] = type;
            }
            catch (Exception ex)
            {
                Log.Global.Error("[ExternalPlugins] failed to register " + type.Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 按名称取本 Agent 的外部插件(不分大小写);未载入返回 null。
    /// </summary>
    public AgentPluginBase? FindExternalPlugin(string name) =>
        _externalPlugins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 批量写入外部插件设置:先整体校验后一次应用(校验失败抛 PluginSettingException,原值不变),
    /// 并把生效值快照回持久化袋。返回本插件的最新描述符供前端刷新。
    /// </summary>
    public IReadOnlyList<PluginSettingDescriptor> ApplyExternalPluginSettings(string pluginName, Dictionary<string, JsonElement> values)
    {
        var plugin = FindExternalPlugin(pluginName)
            ?? throw new InvalidOperationException("外部插件不存在:" + pluginName);
        plugin.SetSettings(values.Select(kv => new KeyValuePair<string, object?>(kv.Key, ExternalPluginValues.FromJson(kv.Value))));
        SnapshotExternalPluginSettings(plugin);
        return plugin.GetSettings();
    }

    /// <summary>设置外部插件启停(经注册对象的官方开关,自动驱动 Initialize/Dispose)并持久化。</summary>
    public void SetExternalPluginEnabled(string pluginName, bool enabled)
    {
        var plugin = FindExternalPlugin(pluginName)
            ?? throw new InvalidOperationException("外部插件不存在:" + pluginName);
        Agent.Plugins.Find(plugin.Name)?.SetEnabled(enabled);
        var bag = EnsureBag(Config, plugin.Name);
        bag.Enabled = enabled;
        Config.Save();
    }

    /// <summary>
    /// 重置外部插件:销毁当前实例并清空持久化设置袋(全部回到默认值),重建新实例重新注册
    /// (保留启停状态)。插件不存在或重建失败返回 false。
    /// </summary>
    public bool ResetExternalPlugin(string pluginName)
    {
        var plugin = FindExternalPlugin(pluginName);
        if (plugin is null || !_externalPluginTypes.TryGetValue(pluginName, out var type))
            return false;
        var reg = Agent.Plugins.Find(plugin.Name);
        var enabled = reg?.Enabled ?? false;
        reg?.Dispose(); // SetEnabled(false) + 移出注册中心
        _externalPlugins.Remove(plugin);
        _externalPluginTypes.Remove(pluginName);
        if (Config.PluginSettings.Remove(pluginName))
            Config.Save(); // 清空持久化袋:设置回到默认
        try
        {
            if (Activator.CreateInstance(type) is not AgentPluginBase fresh)
                return false;
            Agent.Plugins.Register(fresh, enabled);
            fresh.AgentId = Config.Id; // 重建实例同样注入(补上此前重置丢失的 AgentId)
            fresh.AgentConfigView = _configView;
            _externalPlugins.Add(fresh);
            _externalPluginTypes[fresh.Name] = type;
            return true;
        }
        catch (Exception ex)
        {
            Log.Global.Error("[ExternalPlugins] failed to reset " + pluginName + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>把插件当前全部设置值快照进持久化袋并落盘(设置页保存与启停共用)。</summary>
    private void SnapshotExternalPluginSettings(AgentPluginBase plugin)
    {
        var bag = EnsureBag(Config, plugin.Name);
        bag.Values.Clear();
        foreach (var s in plugin.GetSettings())
            bag.Values[s.Name] = ExternalPluginValues.ToJson(ExternalPluginValues.Normalize(s.CurrentValue));
        Config.Save();
    }

    private static ExternalPluginSettings EnsureBag(AgentConfig config, string pluginName)
    {
        if (!config.PluginSettings.TryGetValue(pluginName, out var bag))
        {
            bag = new ExternalPluginSettings();
            config.PluginSettings[pluginName] = bag;
        }
        bag.Values ??= new(StringComparer.OrdinalIgnoreCase);
        return bag;
    }

    // ───────── 前端数据面(侧边栏数据区与 /plugins/{name}/data 端点共用) ─────────

    /// <summary>提供前端数据的启用插件数据面快照(单插件失败只记日志,不影响其他项)。</summary>
    public List<PluginDataDto> PluginDataSnapshots(string? sessionId)
    {
        var list = new List<PluginDataDto>();
        foreach (var registration in Agent.Plugins.EnabledPlugins)
        {
            if (registration.Plugin is not AgentPluginBase { ProvidesData: true } plugin)
                continue;
            if (PluginUi.HasSlot(plugin, "sidebar"))
                continue; // 有 sidebar UI 插槽的插件单开自定义页签,不进 JSON 数据区(避免重复)
            try
            {
                list.Add(new PluginDataDto(registration.Name, plugin.GetPluginData(sessionId)));
            }
            catch (Exception ex)
            {
                Log.Global.Error($"[Plugin] {registration.Name} GetPluginData failed: {ex}");
            }
        }
        return list;
    }

    /// <summary>底部状态栏插件项:启用且 ProvidesData 的插件,GetPluginData 返回对象含非空 status 字符串字段时收录。
    /// 与 PluginDataSnapshots 的区别:不排除 sidebar 插槽插件(它们不进 JSON 数据区,但状态栏需要其 status)。</summary>
    public List<StatusPluginDto> StatusPluginEntries(string? sessionId)
    {
        var list = new List<StatusPluginDto>();
        foreach (var registration in Agent.Plugins.EnabledPlugins)
        {
            if (registration.Plugin is not AgentPluginBase { ProvidesData: true } plugin)
                continue;
            try
            {
                var data = plugin.GetPluginData(sessionId);
                if (data is null) continue;
                var val = data.GetType().GetProperty("status")?.GetValue(data) as string;
                if (!string.IsNullOrEmpty(val))
                    list.Add(new StatusPluginDto(registration.Name, val));
            }
            catch (Exception ex)
            {
                Log.Global.Error($"[Plugin] {registration.Name} GetPluginData failed: {ex}");
            }
        }
        return list;
    }

    /// <summary>声明 sidebar UI 插槽且启用、携带 ui.* 资产的插件(侧边栏单开自定义页签)。</summary>
    public List<PluginUiDto> SidebarUiPlugins()
    {
        var list = new List<PluginUiDto>();
        foreach (var registration in Agent.Plugins.EnabledPlugins)
            if (registration.Plugin is AgentPluginBase p && PluginUi.HasUi(p) && PluginUi.HasSlot(p, "sidebar"))
                list.Add(new PluginUiDto(registration.Name, p.UiSlots));
        return list;
    }

    /// <summary>解析单插件数据面的目标插件(UI 线程调用,注册表读取保持线程亲和);
    /// 插件不存在抛 InvalidOperationException,由端点转 404。</summary>
    public AgentPluginBase ResolveSinglePlugin(string pluginName)
    {
        if (Agent.Plugins.Find(pluginName)?.Plugin is not AgentPluginBase plugin)
            throw new InvalidOperationException("plugin not found: " + pluginName);
        return plugin;
    }

    /// <summary>调用插件数据提供器(后台线程执行):外部插件的数据拉取可能 spawn 子进程
    /// (如 NvidiaMonitor 每次 nvidia-smi,GPU 繁忙时数秒),此前在 Dispatcher.Invoke 里
    /// 同步跑,把 UI 线程整条堵死——表现为所有请求 pending、全部 agent 无法操作。</summary>
    public object? InvokePluginData(AgentPluginBase plugin, string? sessionId)
    {
        try
        {
            return plugin.GetPluginData(sessionId);
        }
        catch (Exception ex)
        {
            Log.Global.Error($"[Plugin] {plugin.Name} GetPluginData failed: {ex}");
            return null;
        }
    }

    /// <summary>调用插件自定义动作(后台线程执行,理由同 <see cref="InvokePluginData"/>)。</summary>
    public object? InvokePluginAction(AgentPluginBase plugin, string? sessionId, string? body)
    {
        try
        {
            return plugin.HandlePluginData(sessionId, body);
        }
        catch (Exception ex)
        {
            Log.Global.Error($"[Plugin] {plugin.Name} HandlePluginData failed: {ex}");
            return null;
        }
    }
}
