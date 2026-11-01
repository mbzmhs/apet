using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Session;
using APet.Plugins.BiliLive;
using APet.Plugins.ContextCompression;
using APet.Plugins.EmotionTags;
using APet.UI.Services;
using APet.UI.Web;

namespace APet.UI.ViewModels;

/// <summary>主窗口 VM:会话列表、当前会话、输入与发送流程。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AgentHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly AgentConfig _agentConfig;
    private readonly UiConfig _config;
    private readonly SessionStore _store;
    private readonly DispatcherTimer _relativeTimeTimer;

    private SessionViewModel? _selectedSession;

    /// <summary>桌宠串行模式的待执行轮次队列:开启桌宠的 Agent 内 TTS/头顶气泡全局唯一,
    /// 同 Agent 多会话同时出声/冒泡会互相打断,后到请求在此排队,当前轮结束后按 FIFO 接续。</summary>
    private readonly Queue<Func<Task>> _pendingTurns = new();

    // 无选中会话时的附件占位(实际总有一个会话;防御性避免空引用)
    private static readonly ObservableCollection<AttachmentViewModel> _emptyAttachments = new();

    public MainViewModel(AgentHost host, Dispatcher dispatcher, AgentConfig agentConfig, UiConfig config)
    {
        _host = host;
        _dispatcher = dispatcher;
        _agentConfig = agentConfig;
        _config = config;
        _store = SessionStore.Shared(agentConfig.Id); // 会话文件按 Agent 稳定 Id 分目录;共享单例:全进程每 Agent 一条 SQLite 连接
        LoadPersistedSessions(); // 恢复上次全部会话;无会话时新建空会话
        ShowCachedModels(); // 工具栏模型列表 = 聊天供应商的模型清单(未选供应商时为空)
        RefreshProviders();
        _ = RefreshModelsAsync(); // 启动即预取当前供应商的模型列表与上下文上限
        _host.EnvContext.WorkingDirectoryProvider = ResolveWorkingDirectory; // 工作目录注入:按会话拼进环境段
        _host.Scene.SceneProvider = ResolveSessionScene;   // 情景注入:按会话拼进 system prompt
        _host.Scene.CommandHandler = ExecuteSceneCommand;  // $scene 指令执行器
        _host.RunRequested += OnLiveRunRequested;
        ValidateBiliBinding(); // 持久化的 $blive 绑定指向已不存在的会话时解绑(经 Changed 事件同步清空持久化)
        _host.HasRunningSessions = () => Sessions.Any(s => s.Session.IsRunning); // 桌宠启停的并发闸门
        _host.LlmConfigApplied += ApplyLlmToSessions; // 设置页保存:各会话按自己的供应商/模型重建连接
        _host.SubAgentAuthPopup += card => AuthPopupRequested?.Invoke(card); // 子代理鉴权卡片走主对话同一弹窗通路
        // 子代理 LLM 连接:子代理自身显式指定供应商时用该供应商(Agent 设定内);
        // 否则供应商/模型/思考等级全部继承发起 sub_run 的主会话所选(会话未选供应商时用 Agent 默认)
        _host.SubAgentLlmFactory = (sessionId, provider, model, effort) =>
        {
            var (vm, hasSessionChoice, prov, m) = ResolveSubAgentLlm(sessionId, provider, model);
            if (!string.IsNullOrWhiteSpace(provider))
                // 显式供应商:思考等级缺省用该供应商默认档位
                return _host.CreateLlmClient(prov, m, string.IsNullOrWhiteSpace(effort) ? prov.ReasoningEffort : effort!);
            var e = string.IsNullOrWhiteSpace(effort) ? (hasSessionChoice ? vm!.ThinkingEffort : null) : effort!;
            return _host.CreateLlmClient(prov, m, e);
        };
        // 子代理用量记账:tokens+费用累计进发起它的主会话,按子代理"生效"供应商/模型计价
        // (可能与主会话不同;解析规则与 LLM 连接同一套:显式指定用指定,空则继承主会话所选)。
        // 子代理连接在其独立会话内固定不变,按生效值计价即按实际使用模型计价;
        // 子代理内部自动压缩的用量也经此通路(压缩插件向运行回调链 ReportUsageAsync)。
        // 回调可能来自后台线程:跳回 UI 线程累计(PropertyChanged 需在 UI 线程触发;
        // AddUsage 内部有锁,主循环/子代理/压缩多写者互不竞态)
        _host.SubAgent.UsageAccounting = (sessionId, provider, model, usage) =>
        {
            var (vm, _, prov, m) = ResolveSubAgentLlm(sessionId, provider, model);
            if (vm is null) return;
            var cost = Services.LlmUsageCost.Of(_agentConfig, prov.Name, m, usage);
            _dispatcher.BeginInvoke(() => vm.AddUsage(usage, cost, updateLiveIndicators: false));
        };
        // 子代理上下文压缩:跟随本 Agent 的压缩设定(总开关/阈值/策略/保留窗口/摘要上限/模板;运行时读取,设置页保存即对新运行生效);
        // 上下文上限按子代理生效的供应商/模型解析(与主会话压缩同一数据源)
        _host.SubAgent.CompressionPluginFactory = (sessionId, provider, model) =>
        {
            if (!_agentConfig.AutoCompress)
                return null;
            var plugin = new ContextCompressionPlugin
            {
                AutoCompress = true,
                CompressThreshold = _agentConfig.CompressThreshold,
                Strategy = _agentConfig.CompressStrategy,
                KeepPercent = _agentConfig.CompressKeepPercent,
                SummaryMaxPercent = _agentConfig.CompressSummaryMaxPercent,
                Template = ContextCompressionPlugin.ResolveTemplate(_agentConfig.CompressPreset, _agentConfig.CompressTemplate),
            };
            plugin.ContextLimit = _ => ResolveSubAgentContextLimit(sessionId, provider, model);
            return plugin;
        };
        // 压缩/记忆上限:按会话各自的模型设定解析(0 = 无上限不压缩)
        _host.ContextCompression.ContextLimit = ResolveContextLimit;
        _host.Memory.ContextLimit = ResolveContextLimitById;
        _host.Memory.CompressThreshold = () => _agentConfig.CompressThreshold;
        // 压缩开始/完成 → 「上下文压缩中」文本行 + 「会话已压缩」标记(见各处理器;中断/失败不留痕迹)
        _host.ContextCompression.CompressionStarted += OnCompressionStarted;
        _host.ContextCompression.CompressionFinished += OnCompressionFinished;
        _host.ContextCompression.CompressionFailed += OnCompressionFailed;
        // 相对时间文本定时刷新:让"刚刚/N分前"随时间推进,无需新消息触发才更新
        // (VM 在 UI 线程构造,无参构造默认绑定当前=UI 调度器)
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _relativeTimeTimer.Tick += (_, _) =>
        {
            foreach (var s in Sessions)
                s.RefreshLastMessageTime();
        };
        _relativeTimeTimer.Start();
    }

    private readonly Dictionary<AgentSession, ChatMessageViewModel> _compressSpinners = new(); // 会话 → 压缩中「上下文压缩中」文本行(临时条目)

    /// <summary>用户是否正在看该会话:以 Web 路由切换时上报的 last-session 为准(主 UI 即 Web,权威信号)。
    /// 不能用 SelectedSession 判 —— 它只被桌面侧(新建/分叉/归档窗口/设置选择器)改写,Web 点选会话
    /// 从不同步,拿它判未读会时灵时不灵(在 A 看着 B 的回复收完,蓝点点不亮)。</summary>
    private bool IsViewing(SessionViewModel session) =>
        string.Equals(_agentConfig.LastSessionId, session.SessionId, StringComparison.Ordinal);

    /// <summary>上下文压缩开始(summarize 策略才有耗时):在聊天记录合适位置放一条"上下文压缩中"文本行(临时、不落盘);
    /// 成功时由完成标记行原位替换,被中断/失败时移除该行,不在记录里留下痕迹。</summary>
    private void OnCompressionStarted(AgentSession agentSession)
    {
        var session = SessionOf(agentSession);
        if (session is null) return;
        RemoveCompressSpinner(session);
        session.IsCompressing = true; // 列表黄点:压缩进行中
        _compressSpinners[agentSession] = session.AddSpinner(CompressionInsertIndex(session));
        WebEventHub.Publish(_agentConfig.Id, session.SessionId, "compress_start"); // Web:刷新快照,显示「上下文压缩中」文本行
    }

    /// <summary>上下文压缩成功完成:移除"上下文压缩中"行,在原位置追加"会话已压缩"标记(落盘保留)。</summary>
    private void OnCompressionFinished(AgentSession agentSession)
    {
        var session = SessionOf(agentSession);
        if (session is null) return;
        RemoveCompressSpinner(session);
        session.CompactedSinceLastSend = true; // 压缩不可撤销:置位后 /undo 需等再发送一条新消息
        if (!IsViewing(session))
            session.Unread = true; // 不在本会话视角时压缩完成(分割线/摘要行是新增内容):标记未读(蓝点)
        var index = CompressionInsertIndex(session);
        session.AddDivider(index);
        // 运行中压缩:后续内容不能继续灌进分割线之前的旧气泡,翻到新气泡承接。
        // 判据必须是「本会话确实存在运行中助手气泡」:手动 /compact 的请求锁(BeginRun)也会置
        // Session.IsRunning,IsStreaming 更是发送锁共用——用它们判断都会给手动压缩错误翻页,
        // 泄漏一个永远停在运行态的空气泡
        if (session.Messages.Any(m => m.Role == MessageRole.Assistant && m.IsBusy))
            session.CompressionBubbleRoll?.Invoke();
        // Web:压缩完成(分割线落位、必要时气泡翻页)后刷新快照对齐
        WebEventHub.Publish(_agentConfig.Id, session.SessionId, "turn_ended");
    }

    /// <summary>上下文压缩失败(已显示"压缩中"但总结未产出/过程异常):把该行原位收口为"压缩失败"分割标记(落盘保留)。</summary>
    private void OnCompressionFailed(AgentSession agentSession)
    {
        var session = SessionOf(agentSession);
        if (session is null) return;
        FailCompressSpinner(session);
        if (!IsViewing(session))
            session.Unread = true; // 新增了一行,不在本会话视角标记未读
        WebEventHub.Publish(_agentConfig.Id, session.SessionId, "turn_ended"); // Web:刷新快照,「压缩失败」行上屏
    }

    /// <summary>移除该会话的压缩齿轮行;failureText 非空 = 压缩未成功即告终(中断/异常兜底),
    /// 用该文字原位收口为分割标记行 ——「压缩已中断」与「压缩失败」经此区分。</summary>
    private void RemoveCompressSpinner(SessionViewModel session, string? failureText = null)
    {
        if (_compressSpinners.TryGetValue(session.Session, out var spinner))
        {
            if (failureText is not null)
                FailCompressSpinner(session, spinner, failureText);
            else
            {
                _compressSpinners.Remove(session.Session);
                session.Messages.Remove(spinner);
            }
            session.IsCompressing = false; // 黄点熄灭(完成/中断/出错都走这里)
        }
    }

    private void FailCompressSpinner(SessionViewModel session, ChatMessageViewModel? spinner = null, string text = "压缩失败")
    {
        if (spinner is null && !_compressSpinners.TryGetValue(session.Session, out spinner))
            return;
        _compressSpinners.Remove(session.Session);
        var index = session.Messages.IndexOf(spinner);
        session.Messages.Remove(spinner);
        if (index >= 0)
            session.AddDivider(text, index);
    }

    /// <summary>压缩指示/标记的插入位置:自动压缩发生在运行内(本轮用户消息与助手占位气泡已入列),
    /// 插到本轮用户消息之前,让标记保持在本轮之上;手动压缩没有新气泡,追加在末尾。</summary>
    private static int CompressionInsertIndex(SessionViewModel session)
    {
        var messages = session.Messages;
        if (messages.Count == 0) return 0;
        if (messages[^1] is { IsBusy: true } last && IsEmpty(last))
            return messages.Count >= 2 && messages[^2].Role == MessageRole.User ? messages.Count - 2 : messages.Count - 1;
        return messages.Count;
    }

    private SessionViewModel? SessionOf(AgentSession agentSession) =>
        Sessions.FirstOrDefault(s => ReferenceEquals(s.Session, agentSession));

    /// <summary>供应商/模型标签(模型切换分割线文案):「供应商名/模型名」;未选供应商时只显示模型名。</summary>
    private static string LlmLabel(string provider, string model) =>
        provider.Length > 0 ? $"{provider}/{model}" : model;

    /// <summary>会话的供应商配置:按会话记录的供应商名查找;未记录/已删除时回退 Agent 生效供应商。</summary>
    private ProviderConfig ProviderFor(SessionViewModel session) =>
        _agentConfig.Llm.Find(session.ChatProvider) ?? _agentConfig.Llm.Active;

    /// <summary>按会话自己的供应商/模型/思考等级重建其 LLM 连接
    /// (轮次进行中由会话锁挂起,本轮保持发起时配置,轮末自动切换)。</summary>
    private void RebuildSessionClient(SessionViewModel session)
    {
        session.Session.UpdateLlmClient(_host.CreateLlmClient(ProviderFor(session), session.ChatModel, session.ThinkingEffort));
    }

    /// <summary>会话级 LLM 选择初始化:优先沿用显式指定的参考会话(Web 侧传入当前浏览会话),
    /// 否则回落当前选中会话(新建/分叉场景);无参考(如启动后首个会话)时留空——
    /// 空供应商+空模型,由用户自行选择(工具栏显示"请选择供应商")。</summary>
    private void SeedSessionLlm(SessionViewModel session, string? fromSessionId = null)
    {
        var source = fromSessionId is null
            ? SelectedSession
            : Sessions.FirstOrDefault(s => s.SessionId == fromSessionId) ?? SelectedSession;
        if (source is not null && !ReferenceEquals(source, session) && source.ChatProvider.Length > 0)
        {
            session.ChatProvider = source.ChatProvider;
            session.ChatModel = source.ChatModel;
            session.ThinkingEffort = source.ThinkingEffort;
        }
        RebuildSessionClient(session);
    }

    /// <summary>上下文压缩插件的 Token 上限(按会话):该会话所选模型的上下文大小(清单保存强制 > 0,必有有效值);
    /// 取消勾选「本地设置优先」时服务端上报值优先(取不到回退手动值)。未选模型(不可发送)返回 0,压缩不触发。</summary>
    private int ResolveContextLimit(AgentSession agentSession)
    {
        var vm = SessionOf(agentSession);
        var provider = vm is null ? _agentConfig.Llm.Active : ProviderFor(vm);
        var model = vm is { ChatModel.Length: > 0 } s ? s.ChatModel : provider.Model;
        var api = ResolveApiContext(provider.Name, model);
        var result = provider.FindModel(model) is { } config
            ? (config.PreferLocal ? (config.ContextTokens > 0 ? config.ContextTokens : api) : api)
            : api; // 不在清单内(旧数据等):仅服务端值
        if (result <= 0)
            APet.Framework.Logging.Log.Global.Debug($"[compression] context limit unresolved: provider={provider?.Name}, model={model}");
        return result;
    }

    /// <summary>Web 会话快照用:本会话生效的上下文上限(未选模型为 0)。</summary>
    public int ContextLimitOf(SessionViewModel session) => ResolveContextLimit(session.Session);

    /// <summary>记忆预算的上下文上限(按会话 Id 解析;工具执行作用域只能拿到会话 Id)。</summary>
    private int ResolveContextLimitById(string sessionId)
    {
        var vm = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        return vm is null ? 0 : ResolveContextLimit(vm.Session);
    }

    /// <summary>子代理生效的 LLM 设定(LLM 连接与压缩上限解析共用):
    /// 显式供应商 → Agent 设定内同名供应商(失配回退默认供应商),模型缺省用该供应商默认模型;
    /// 未显式指定 → 供应商/模型继承发起 sub_run 的主会话所选(会话未选供应商时用 Agent 默认)。</summary>
    private (SessionViewModel? Vm, bool HasSessionChoice, ProviderConfig Prov, string Model)
        ResolveSubAgentLlm(string? sessionId, string? provider, string? model)
    {
        var vm = sessionId is { Length: > 0 } id ? Sessions.FirstOrDefault(s => s.SessionId == id) : null;
        vm ??= SelectedSession;
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var explicitProv = _agentConfig.Llm.Providers
                .FirstOrDefault(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase))
                ?? _agentConfig.Llm.Active;
            return (vm, false, explicitProv, string.IsNullOrWhiteSpace(model) ? explicitProv.Model : model!);
        }
        var hasSessionChoice = vm is { ChatProvider.Length: > 0 };
        var prov = hasSessionChoice ? ProviderFor(vm) : _agentConfig.Llm.Active;
        var m = string.IsNullOrWhiteSpace(model) ? (hasSessionChoice ? vm!.ChatModel : prov.Model) : model!;
        return (vm, hasSessionChoice, prov, m);
    }

    /// <summary>子代理上下文上限(与主会话压缩上限同一数据源):生效模型的上下文大小
    /// (「本地设置优先」时取清单值、取不到回退服务端上报值;否则服务端值);解析不到为 0(压缩不触发)。</summary>
    private int ResolveSubAgentContextLimit(string? sessionId, string? provider, string? model)
    {
        var (_, _, prov, m) = ResolveSubAgentLlm(sessionId, provider, model);
        var api = ResolveApiContext(prov.Name, m);
        if (prov.FindModel(m) is not { } config)
            return api;
        return config.PreferLocal ? (config.ContextTokens > 0 ? config.ContextTokens : api) : api;
    }

    /// <summary>输入卡片工具栏:聊天模型(会话级:只改当前选中会话)。只能从该供应商的模型清单中选择;
    /// 未选择供应商时为空,聊天界面禁止发送。改动即时重建本会话连接并随会话落盘。</summary>
    public string SelectedModel
    {
        get => SelectedSession is { ChatModel.Length: > 0 } s ? s.ChatModel : "";
        set
        {
            var session = SelectedSession;
            if (session is null || session.ChatProvider.Length == 0)
                return; // 未选择供应商:不设模型
            var model = (value ?? "").Trim();
            if (model.Length == 0 || model == session.ChatModel)
                return;
            if (ProviderFor(session).Models.Count > 0 && ProviderFor(session).FindModel(model) is null)
                return; // 只允许选择清单内的模型
            session.ChatModel = model;
            RebuildSessionClient(session);
            SaveSession(session); // 会话级选择随会话落盘
            Raise(nameof(SelectedModel));
            RefreshContextUsage();
        }
    }

    /// <summary>输入卡片工具栏:聊天供应商(会话级:只改当前选中会话,每个会话可用不同模型同时工作);
    /// 空 = 未选择,界面显示"请选择供应商"且禁止发送。切换后重建本会话连接并随会话落盘。</summary>
    public string SelectedProvider
    {
        get => SelectedSession?.ChatProvider ?? "";
        set
        {
            var session = SelectedSession;
            if (session is null)
                return;
            var target = _agentConfig.Llm.Find(value);
            if (target is null || value == session.ChatProvider)
                return;
            session.ChatProvider = target.Name;
            session.ChatModel = target.Model; // 换供应商:模型回落该供应商默认
            if (session.ThinkingEffort.Length == 0)
                session.ThinkingEffort = target.ReasoningEffort; // 选定供应商时物化思考等级(供应商默认,通常 high)
            RebuildSessionClient(session);
            SaveSession(session);
            Raise(nameof(SelectedProvider));
            Raise(nameof(SelectedModel)); // 模型清单随供应商
            ShowCachedModels();
            RefreshContextUsage();
            _ = RefreshModelsAsync(); // 该供应商的模型列表/上下文大小(有缓存则跳过)
        }
    }

    /// <summary>输入卡片工具栏:思考等级(关闭/开启(默认,按 high 发送)/minimal/low/medium/high/xhigh/max),会话级:只改当前选中会话。
    /// 会话未设置时显示供应商默认值("" 不显示为"关闭思考"——实际请求本就走供应商默认);改动重建本会话连接并随会话落盘。</summary>
    public string ThinkingEffort
    {
        get => SelectedSession is { ChatProvider.Length: > 0 } s
            ? ProviderConfig.NormalizeReasoningEffort(
                s.ThinkingEffort.Length > 0 ? s.ThinkingEffort : ProviderFor(s).ReasoningEffort)
            : "high";
        set
        {
            var session = SelectedSession;
            if (session is null || session.ChatProvider.Length == 0)
                return; // 未选择供应商:不设思考等级
            var effort = ProviderConfig.NormalizeReasoningEffort(value);
            if (effort == ProviderConfig.NormalizeReasoningEffort(session.ThinkingEffort))
                return; // 归一化后无变化(含大小写/旧值差异)则不重复应用
            session.ThinkingEffort = effort;
            RebuildSessionClient(session);
            SaveSession(session);
            Raise(nameof(ThinkingEffort));
        }
    }

    /// <summary>工作目录注入:按会话返回裸路径(格式化由 EnvContext 环境段统一负责;未设置不注入)。</summary>
    private string? ResolveWorkingDirectory(AgentSession agentSession)
    {
        var vm = Sessions.FirstOrDefault(s => ReferenceEquals(s.Session, agentSession));
        return vm is null || string.IsNullOrWhiteSpace(vm.WorkingDirectory) ? null : vm.WorkingDirectory;
    }

    /// <summary>情景注入:返回该会话当前生效的情景文本(空 = 不注入);多次 $scene 以最后一次为准。</summary>
    private string? ResolveSessionScene(AgentSession agentSession)
    {
        var vm = Sessions.FirstOrDefault(s => ReferenceEquals(s.Session, agentSession));
        return string.IsNullOrWhiteSpace(vm?.Scene) ? null : vm.Scene;
    }

    /// <summary>$scene 指令:按名注入预设情景(多次调用以最后一次为准),空参数清空;随会话持久化。</summary>
    private string? ExecuteSceneCommand(string? sessionId, string args)
    {
        var session = Sessions.FirstOrDefault(s => s.SessionId == sessionId) ?? SelectedSession;
        if (session is null)
            return "无法操作:当前没有会话";

        var name = args.Trim();
        if (name.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(session.Scene))
                return "当前会话未注入情景";
            session.Scene = "";
            _store.Save(BuildStored(session));
            return "已清空会话情景";
        }

        var preset = _agentConfig.ScenePresets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            var names = _agentConfig.ScenePresets.Select(p => p.Name).ToList();
            return names.Count == 0
                ? "还没有预设情景:请在 设置 → 插件:情景 中创建"
                : $"没有名为「{name}」的预设情景。可用:{string.Join("、", names)}";
        }
        session.Scene = preset.Content;
        _store.Save(BuildStored(session));
        return $"已注入情景「{preset.Name}」";
    }

    /// <summary>设置页供应商配置变更(增删改/地址/密钥/思考字段开关):每个会话按自己的
    /// 供应商/模型重建连接(供应商被删除的会话回退 Agent 生效供应商),并同步工具栏可选项。</summary>
    private void ApplyLlmToSessions()
    {
        foreach (var session in Sessions)
            RebuildSessionClient(session);

        RefreshProviders();
        ShowCachedModels();
        Raise(nameof(ThinkingEffort)); // 思考等级随会话,同步聊天框
        _ = RefreshModelsAsync(); // 补拉涉及供应商的模型列表与上下文大小(有缓存则跳过)
        RefreshContextUsage();
    }

    /// <summary>供应商列表(设置页增删改或切换后同步到聊天工具栏)。</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AvailableProviders { get; } = new();

    /// <summary>从配置重建供应商列表(不触发选中变化,只同步可选项与当前值)。</summary>
    public void RefreshProviders()
    {
        var names = _agentConfig.Llm.Providers.Select(p => p.Name).ToList();
        if (AvailableProviders.Count == names.Count
            && AvailableProviders.SequenceEqual(names, StringComparer.Ordinal))
            return;
        AvailableProviders.Clear();
        foreach (var name in names)
            AvailableProviders.Add(name);
    }

    /// <summary>停止指定会话(运行中:中断请求;排队中:取消该轮并清理占位;插话队列一律清空)。
    /// Web 与工具栏共用。排队中分支的清理之外,运行中分支由 RunTurnAsync 收尾(中断)再清一次兜底。</summary>
    public void StopSession(SessionViewModel session)
    {
        session.Session.CancelRun(); // 运行中:中断请求(排队中尚未开跑:空操作)
        session.Session.ClearQueue(); // 手动终止:插话队列全部清空(不再随下次发送带出,也不被自动派发)
        if (session.IsStreaming && !session.Session.IsRunning && session.PendingAssistant is { } pending)
        {
            // 排队中尚未开跑:取消该轮;出队时 RunTurnAsync 按标记丢弃
            session.PendingTurnCancelled = true;
            session.IsStreaming = false;
            pending.EndThinking();
            pending.IsBusy = false;
            session.Messages.Remove(pending);
            session.AddStatus("已中断");
            session.PendingAssistant = null;
            SaveSession(session);
            // Web 收口:此路径没有框架的 complete/error 帧,主动发一条让浏览器 refetch,
            // 停止按钮与输入框运行齿轮一并复位(并露出"已中断"状态行)
            WebEventHub.Publish(_agentConfig.Id, session.SessionId, "turn_ended");
        }
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = new();

    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            var old = _selectedSession;
            if (Set(ref _selectedSession, value, nameof(SelectedSession)))
            {
                if (old is not null)
                    old.PropertyChanged -= OnSelectedSessionPropertyChanged;
                if (_selectedSession is not null)
                {
                    _selectedSession.PropertyChanged += OnSelectedSessionPropertyChanged;
                    // 切到该会话 = 已看到:熄灭未读(蓝)与出错(红)提示点
                    _selectedSession.Unread = false;
                    _selectedSession.HasError = false;
                }
                // 工具栏三件套(供应商/模型/思考等级)与模型清单跟随当前选中会话
                Raise(nameof(SelectedProvider));
                Raise(nameof(SelectedModel));
                Raise(nameof(ThinkingEffort));
                ShowCachedModels();
                _ = RefreshModelsAsync(); // 该会话供应商的模型列表(有缓存则跳过)
                Raise(nameof(SessionWorkingDirectory));
                Raise(nameof(SessionWorkingDirectoryAccess));
                Raise(nameof(ContextUsageText));
                // 会话切换:输入框文本与附件缩略图行各自跟随本会话草稿
                Raise(nameof(Input));
                Raise(nameof(Attachments));
                RaiseAttachments();
            }
        }
    }

    /// <summary>会话内的工作目录/权限变化同步到工具栏(按钮文本/锁定状态)。</summary>
    private void OnSelectedSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.LastPromptTokens))
        {
            RefreshContextUsage();
            return;
        }
        if (e.PropertyName is nameof(SessionViewModel.WorkingDirectory) or nameof(SessionViewModel.IsWorkDirLocked))
            Raise(nameof(SessionWorkingDirectory));
        else if (e.PropertyName is nameof(SessionViewModel.WorkingDirectoryAccess))
            Raise(nameof(SessionWorkingDirectoryAccess));
    }

    /// <summary>当前会话的工作目录(会话级设定;首次对话后锁定)。</summary>
    public string SessionWorkingDirectory
    {
        get => SelectedSession?.WorkingDirectory ?? "";
        set
        {
            if (SelectedSession is { IsWorkDirLocked: false } session)
            {
                session.WorkingDirectory = value;
                _store.Save(BuildStored(session)); // 立即落盘:不发消息就重启也不丢
            }
        }
    }

    /// <summary>当前会话的工作目录权限:ReadOnly / ReadWrite / FullAccess。</summary>
    public string SessionWorkingDirectoryAccess
    {
        get => SelectedSession?.WorkingDirectoryAccess ?? "ReadWrite";
        set
        {
            if (SelectedSession is { } session)
            {
                session.WorkingDirectoryAccess = value;
                _store.Save(BuildStored(session)); // 立即落盘:不发消息就重启也不丢
            }
        }
    }

    /// <summary>输入框草稿文本:透传当前会话的 DraftText(每个会话独立,切换会话时由 SelectedSession 变更通知刷新)。</summary>
    public string Input
    {
        get => SelectedSession?.DraftText ?? "";
        set
        {
            if (SelectedSession is not null)
                SelectedSession.DraftText = value;
            Raise(nameof(Input));
        }
    }

    // ───────── 输入卡片附件:图片(多模态)/ 文本文件(内联正文) ─────────

    private const long MaxImageBytes = 20L * 1024 * 1024;   // 单张图片上限 20 MB
    private const long MaxTextBytes = 512L * 1024;          // 单个文本文件上限 512 KB

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
          ".ini", ".log", ".srt", ".vtt", ".c", ".h", ".cpp", ".cs", ".js", ".ts",
          ".py", ".sh", ".bat", ".ps1" };

    /// <summary>待发送附件:透传当前会话的 DraftAttachments(每个会话独立;发送时消费)。</summary>
    public ObservableCollection<AttachmentViewModel> Attachments => SelectedSession?.DraftAttachments ?? _emptyAttachments;

    /// <summary>是否有待发送附件(驱动缩略图行可见性)。</summary>
    public bool HasAttachments => Attachments.Count > 0;

    private void RaiseAttachments() => Raise(nameof(HasAttachments));

    /// <summary>聊天记录是否隐藏思考块(仅显示层,持久化记录仍全量保存思考内容);取自 Agent 配置。</summary>
    public bool HideThinking => _agentConfig.HideThinking;

    /// <summary>设置页热切换显示选项后调用:通知 UI 重新评估思考块可见性(XAML DataTrigger 绑定本属性)。</summary>
    public void RefreshDisplayOptions() => Raise(nameof(HideThinking));

    /// <summary>按扩展名路由拖入/选中的文件:图片 → 图片附件,文本 → 文本文件附件;全部为不支持类型时状态行提示。</summary>
    public void AddDroppedFiles(IEnumerable<string> paths)
    {
        var unsupported = new List<string>();
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ImageExtensions.Contains(ext))
                AddImageFiles(new[] { path });
            else if (TextExtensions.Contains(ext))
                AddTextFile(path);
            else
                unsupported.Add(Path.GetFileName(path));
        }
        if (unsupported.Count > 0)
            Warn($"不支持的文件类型,未添加:{string.Join("、", unsupported.Take(3))}{(unsupported.Count > 3 ? " 等" : "")}");
    }

    /// <summary>从文件路径添加图片(菜单选择/拖放);超限或读取失败以状态行提示。</summary>
    public void AddImageFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try
            {
                if (!ImageExtensions.Contains(Path.GetExtension(path)))
                    continue;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxImageBytes)
                {
                    Warn($"图片「{name}」超过 {FormatSize(MaxImageBytes)} 上限,未添加。");
                    continue;
                }
                // 预览解码失败(如 WebP/特殊 PNG,WPF 无对应解码器)不阻断:仍作为附件发送并持久化,缩略图以占位显示
                ImageSource? preview = null;
                try
                {
                    using var stream = new MemoryStream(bytes);
                    preview = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
                catch (Exception ex)
                {
                    Warn($"图片「{name}」无法生成预览({ex.Message}),将直接发送。");
                }
                Attachments.Add(new AttachmentViewModel(AttachmentKind.Image, name, preview)
                {
                    DataUrl = "data:" + ImageMime(Path.GetExtension(path)) + ";base64," + Convert.ToBase64String(bytes),
                    SizeText = FormatSize(bytes.Length),
                });
            }
            catch (Exception ex)
            {
                Warn($"读取图片「{name}」失败:{ex.Message}");
            }
        }
        RaiseAttachments();
    }

    /// <summary>从剪贴板添加图片(Ctrl+V):编码为 PNG data URL。</summary>
    public void AddClipboardImages(IEnumerable<ImageSource> images)
    {
        var index = 0;
        foreach (var image in images)
        {
            try
            {
            if (image is not BitmapSource bitmap)
                continue; // 剪贴板位图实际均为 BitmapSource,防御性跳过异常类型
            bitmap = NormalizeClipboardBitmap(bitmap);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            Attachments.Add(new AttachmentViewModel(AttachmentKind.Image, $"剪贴板图片 {++index}", bitmap)
            {
                DataUrl = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()),
                SizeText = FormatSize(ms.Length),
            });
            }
            catch (Exception ex)
            {
                Warn($"剪贴板图片添加失败:{ex.Message}");
            }
        }
        RaiseAttachments();
    }

    /// <summary>
    /// 剪贴板位图归一化:Windows 截图/复制产生的 32bpp DIB 其 alpha 字节通常为常量 0(GDI 语义=不透明),
    /// WPF 按 premultiplied Bgra32 解读会渲染成全透明(缩略图空白、模型也看不到)。
    /// alpha 通道为常量(全 0/全 255)时转成无 alpha 的 Bgr32 找回原色;alpha 有变化(真透明图)则保留。
    /// </summary>
    private static BitmapSource NormalizeClipboardBitmap(BitmapSource src)
    {
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
            return src; // 无 alpha 通道(如 JPEG/24bpp DIB):原样使用
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w == 0 || h == 0)
            return src;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var firstAlpha = px[3]; // alpha 为每像素第 4 字节
        for (int i = 7; i < px.Length; i += 4)
            if (px[i] != firstAlpha)
                return src; // alpha 有变化:真实透明图,保留原样
        var opaque = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    /// <summary>添加文本文件:气泡内显示为文件卡片,内容在发送时内联进模型正文(任意模型可用,无需多模态支持)。</summary>
    public void AddTextFile(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            if (!TextExtensions.Contains(Path.GetExtension(path)))
                return;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxTextBytes)
            {
                Warn($"文本文件「{name}」超过 {FormatSize(MaxTextBytes)} 上限,未添加。");
                return;
            }
            Attachments.Add(new AttachmentViewModel(AttachmentKind.TextFile, name, null)
            {
                TextContent = File.ReadAllText(path),
                SizeText = FormatSize(bytes.Length),
            });
        }
        catch (Exception ex)
        {
            Warn($"读取文本文件「{name}」失败:{ex.Message}");
        }
        RaiseAttachments();
    }

    /// <summary>删除一个待发送附件(缩略图上的叉号)。</summary>
    public void RemoveAttachment(AttachmentViewModel item)
    {
        if (Attachments.Remove(item))
            RaiseAttachments();
    }

    /// <summary>发送时消费全部附件(组装进消息后清空缩略图行)。</summary>
    internal void ClearAttachments()
    {
        Attachments.Clear();
        RaiseAttachments();
    }

    /// <summary>附件相关的失败提示:以状态行显示在当前会话。</summary>
    private void Warn(string message) => SelectedSession?.AddStatus("⚠ " + message);

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : $"{Math.Max(1, bytes / 1024.0):0.#} KB";

    private static string ImageMime(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>新对话:创建框架会话并选中;Agent 设置了默认工作目录时,新会话继承目录与权限。
    /// fromSessionId:Web 侧显式指定继承源(当前浏览的会话)——WPF 的 SelectedSession 与 Web 浏览的会话
    /// 可能不同步,缺省时才回落到 SelectedSession。folderId:新对话直接建在该文件夹里(缺省根组)。</summary>
    public SessionViewModel NewSession(string? fromSessionId = null, string? folderId = null)
    {
        var session = new SessionViewModel(_host.Agent.CreateSession());
        var defaultDir = _agentConfig.Security.DefaultWorkingDirectory;
        if (!string.IsNullOrWhiteSpace(defaultDir))
        {
            session.WorkingDirectory = defaultDir;
            session.WorkingDirectoryAccess = _agentConfig.Security.DefaultWorkingDirectoryAccess;
        }
        SeedSessionLlm(session, fromSessionId);
        AttachSession(session);
        Sessions.Add(session);
        // 列表布局:新对话 append 到目标组末尾(指定文件夹不存在时回根)
        _store.AppendSessionSlot(session.SessionId, folderId is not null && _store.FolderExists(folderId) ? folderId : null);
        SaveSession(session); // 空对话也立即落盘:重启后仍留在列表里
        SelectedSession = session;
        return session;
    }

    /// <summary>分叉当前选中会话:新建一个会话,框架上下文/显示气泡/工作目录等全部相同,仅会话 Id 不同(独立文件);
    /// 名字取参数,缺省为「Fork of 原会话名」。返回新会话;无选中会话时返回 null。</summary>
    public SessionViewModel? ForkSession(string? name) => ForkSession(SelectedSession, name);

    /// <summary>分叉指定会话(Web 与聊天 /fork 共用)。运行中的会话拒绝:复制到一半的轮次
    /// 会把半截上下文当成完整历史。</summary>
    public SessionViewModel? ForkSession(SessionViewModel? source, string? name)
    {
        if (source is null || source.IsStreaming || source.Session.IsRunning) return null;

        var newAgent = _host.Agent.CreateSession(); // 全新 GUID → 独立持久化文件,绝不覆盖原会话
        var forked = new SessionViewModel(newAgent, newAgent.Id, source.CreatedAt); // 保留原创建时间
        AttachSession(forked);

        forked.Title = string.IsNullOrWhiteSpace(name) ? "Fork of " + source.Title : name.Trim();

        // 会话级设置一并复制(工作目录/权限/白名单/锁定;压缩标记跨分叉保留,维持 /undo 规则)
        forked.WorkingDirectory = source.WorkingDirectory;
        forked.WorkingDirectoryAccess = source.WorkingDirectoryAccess;
        if (source.IsWorkDirLocked) forked.LockWorkDirectory();
        forked.WhitelistedDirectories.AddRange(source.WhitelistedDirectories);
        forked.CompactedSinceLastSend = source.CompactedSinceLastSend;
        // 供应商/模型/思考等级一并复制后按新选择重建连接;上一轮实际模型同复制(切换分割线语义随历史继承)
        forked.ChatProvider = source.ChatProvider;
        forked.ChatModel = source.ChatModel;
        forked.ThinkingEffort = source.ThinkingEffort;
        forked.LastRoundProvider = source.LastRoundProvider;
        forked.LastRoundModel = source.LastRoundModel;
        RebuildSessionClient(forked);

        // 图片目录整体复制到新 Id(内容哈希同名幂等):显示气泡立即可读回原图
        _store.CopySessionImages(source.SessionId, forked.SessionId);
        // TODO 清单一并复制到新 Id:分叉会话继承原任务清单(此后各自独立演进)
        _store.CopySessionTodo(source.SessionId, forked.SessionId);

        // 框架上下文深拷贝(独立实例,避免与原会话共享同一批消息对象)
        forked.Session.AddMessages(source.Session.Messages.Select(SessionStore.CloneMessage).ToList());

        // 显示气泡复用还原逻辑重建:图片/文本附件/步骤流/情绪 tag 剥离与原会话一致
        foreach (var m in source.Messages.Where(m => !m.Ephemeral))
            RestoreMessage(forked, ToStoredMessage(m));

        Sessions.Add(forked);
        // 列表布局:分叉会话跟随源会话所在组;源组已不存在(归档期间文件夹被删)则回根
        var slots = _store.ListSessionSlots();
        var sourceFolder = slots.TryGetValue(source.SessionId, out var sl)
            && sl.FolderId is { } fid && _store.FolderExists(fid) ? fid : null;
        _store.AppendSessionSlot(forked.SessionId, sourceFolder);
        SelectedSession = forked;
        SaveSession(forked); // 立即落盘(上下文图片按新 Id 外置),重启不丢
        forked.AddStatus($"已从「{source.Title}」分叉"); // 仅显示层,不进 LLM 上下文
        return forked;
    }

    /// <summary>订阅会话消息流(自动滚动通知 + 上下文占用刷新);新会话与还原会话共用。</summary>
    private void AttachSession(SessionViewModel session)
    {
        session.Messages.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                MessageAdded?.Invoke(session, e);
            RefreshContextUsage();
        };
        // 中途消息队列:入队/移除 → 事件自带队列快照,前端就地打补丁(不整树刷新——
        // 运行期整树 refetch 会与在途的 tool_finished 帧竞态,旧快照把已收口的工具行盖回"运行中");
        // 被某次请求消费 → 载荷同时带上插话气泡与插入位置,前端按锚点规则插入
        session.Session.QueueChanged += () =>
            WebEventHub.Publish(_agentConfig.Id, session.SessionId, "queue_changed", new
            {
                queuedMessages = SessionSnapshot.QueuedMessagesOf(session),
            });
        session.Session.QueueDrained += (items, atRunStart) =>
            OnQueueDrained(session, items, atRunStart);
        // 会话列表小点:小点相关属性一变即广播 Agent 级 sessions_changed,前端强刷列表,
        // 状态点不再干等 5 秒轮询(轮询保留,兜底相对时间文本与建删等边角)
        session.PropertyChanged += OnSessionListItemChanged;
    }

    /// <summary>会话小点相关属性(绿=运行、黄=压缩、红=出错、蓝=未读;标题随行)变化:
    /// 广播 Agent 级 sessions_changed,所有打开的窗口立即强刷会话列表。
    /// 收尾时多属性连发(IsStreaming/HasError/Unread)由前端合并成一次强刷。</summary>
    private void OnSessionListItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SessionViewModel.Title) or nameof(SessionViewModel.IsStreaming)
            or nameof(SessionViewModel.IsCompressing) or nameof(SessionViewModel.HasError)
            or nameof(SessionViewModel.Unread)))
            return;
        WebEventHub.PublishAgent(_agentConfig.Id, "sessions_changed");
    }

    /// <summary>队列被某次请求消费:为每条插话补用户气泡(context 消息同 id ↔ 气泡 ClientId),
    /// 并把气泡与插入模式随 queue_changed 事件下发,前端按同一锚点规则就地插入(不整树刷新)。
    /// 开跑组装时消费(条目早于输入)插到本轮输入气泡之前;开跑后消费(首轮/工具轮)先给当前助手气泡
    /// 翻页——已产生的思考/工具步骤定格,插话落在其后,后续步骤进新气泡(展示顺序 = 步骤在上、
    /// 插话在下,与真实时序一致)。</summary>
    private void OnQueueDrained(SessionViewModel session, IReadOnlyList<QueuedUserMessage> items, bool atRunStart)
    {
        if (!atRunStart)
        {
            // 气泡翻页(与运行中压缩同一机制):定格当前助手气泡的已有步骤;空气泡内部返回不翻
            var prev = session.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
            session.CompressionBubbleRoll?.Invoke();
            prev?.EndThinking(); // 翻页 lambda 只清 IsBusy:定格气泡的思考计时在此收口
        }
        ChatMessageViewModel? anchor = atRunStart
            ? session.Messages.LastOrDefault(m => m.Role == MessageRole.User)
            : session.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        var index = anchor is null ? session.Messages.Count : session.Messages.IndexOf(anchor);
        var created = new List<ChatMessageViewModel>(items.Count);
        foreach (var item in items)
        {
            var bubble = CreateQueuedUserBubble(session, item);
            session.Messages.Insert(index++, bubble);
            created.Add(bubble);
        }
        WebEventHub.Publish(_agentConfig.Id, session.SessionId, "queue_changed", new
        {
            queuedMessages = SessionSnapshot.QueuedMessagesOf(session),
            insert = new
            {
                mode = atRunStart ? "beforeLastUser" : "afterLastAssistant",
                items = created.Select(m => SessionSnapshot.Message(m)).ToList(),
            },
        });
    }

    /// <summary>队列条目 → 用户气泡:**只构造不入列**(AddUser 系列会追加进列表,再按锚点 Insert 会造成
    /// 同一实例列两次);入列由调用方各做一次——消费路径按锚点插入,派发路径追加到尾部。
    /// 气泡 ClientId = 队列条目 id,与 context 消息 Id 对应(display↔context 匹配,/undo 同规则)。</summary>
    private ChatMessageViewModel CreateQueuedUserBubble(SessionViewModel session, QueuedUserMessage item)
    {
        var bubble = new ChatMessageViewModel(MessageRole.User, item.Text);
        foreach (var dataUrl in item.Images ?? [])
        {
            bubble.Images.Add(PlaceholderImage);
            bubble.ImageFiles.Add(_store.StoreImage(session.SessionId, DecodeDataUrlBytes(dataUrl), ExtFromDataUrl(dataUrl)));
        }
        bubble.ClientId = item.Id;
        foreach (var file in item.TextFiles ?? [])
            bubble.TextFiles.Add(new TextFileAttachmentViewModel
            {
                Name = file.Name,
                SizeText = HumanSize(Encoding.UTF8.GetByteCount(file.Content)),
                Content = file.Content,
            });
        return bubble;
    }

    /// <summary>data URL → 入库扩展名(图片按子类型,jpeg 归一 .jpg;PDF 给 .pdf;其余给 .bin,仅影响文件名)。</summary>
    private static string ExtFromDataUrl(string dataUrl)
    {
        const string imagePrefix = "data:image/";
        if (dataUrl.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var end = dataUrl.IndexOf(';', imagePrefix.Length);
            var mime = dataUrl.Substring(imagePrefix.Length,
                (end < 0 ? dataUrl.Length : end) - imagePrefix.Length).ToLowerInvariant();
            return "." + (mime == "jpeg" ? "jpg" : mime);
        }
        return dataUrl.StartsWith("data:application/pdf", StringComparison.OrdinalIgnoreCase) ? ".pdf" : ".bin";
    }

    /// <summary>字节数 → 人类可读大小(队列气泡的文本文件卡片)。</summary>
    private static string HumanSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F1} KB"
        : $"{bytes} B";

    // ───────── 会话持久化(每 Agent 每 Session 一个 JSON 文件) ─────────

    /// <summary>会话存储(归档窗口读取已归档会话用)。</summary>
    public SessionStore Store => _store;

    /// <summary>启动时从磁盘恢复本 Agent 全部未归档会话(按更新时间升序,选中最近的一个);无会话时新建。</summary>
    private void LoadPersistedSessions()
    {
        foreach (var stored in _store.LoadAll().Where(s => !s.Archived))
            Sessions.Add(RestoreSession(stored));
        // 列表布局补槽:旧库(尚无文件夹/排序数据)按当前顺序补根槽,首启顺序与旧行为一致;
        // 已有槽位的会话/文件夹保留原归属/排序,重启后布局原样还原
        if (Sessions.Count > 0)
            _store.EnsureSessionSlots(Sessions.Select(s => (s.SessionId, (string?)null)));
        _store.EnsureFolderSlots();
        if (Sessions.Count == 0)
        {
            NewSession(); // 内部已补根槽
            return;
        }
        SelectedSession = Sessions[^1];
    }

    /// <summary>从持久化数据还原会话:框架上下文原样恢复(续聊不丢记忆),气泡按步骤流重建。</summary>
    private SessionViewModel RestoreSession(StoredSession stored)
    {
        var session = new SessionViewModel(_host.Agent.CreateSession(), stored.Id, stored.CreatedAt);
        AttachSession(session);
        session.Title = stored.Title;
        session.CompactedSinceLastSend = stored.CompactedSinceLastSend; // 压缩标记跨重启保留:/undo 规则不因重开失效
        session.Scene = stored.Scene; // 注入的情景随会话恢复(重启后继续生效)
        session.WorkingDirectory = stored.WorkingDirectory; // 工作目录/权限/信任白名单随会话恢复(重启不丢)
        session.WorkingDirectoryAccess = stored.WorkingDirectoryAccess;
        // 会话级供应商/模型/思考等级恢复;旧文件无此字段(空)时留空,由用户重新选择
        session.ChatProvider = stored.ChatProvider;
        session.ChatModel = stored.ChatModel;
        session.ThinkingEffort = stored.ThinkingEffort;
        session.LastRoundProvider = stored.LastRoundProvider; // 旧文件无此字段 = 空,首轮不出分割线
        session.LastRoundModel = stored.LastRoundModel;
        if (stored.LastPromptTokens > 0)
        {
            session.LastPromptTokens = (int)Math.Min(stored.LastPromptTokens, int.MaxValue); // 占用条按真实值显示
            session.Session.RestoreLastPromptTokens(stored.LastPromptTokens); // 框架侧同步:开跑时的压缩检查不因重启归零
        }
        session.UsagePromptTotal = stored.UsagePromptTotal; // 会话累计用量随会话还原(状态栏重开继续显示)
        session.UsageCachedTotal = stored.UsageCachedTotal;
        session.UsageCompletionTotal = stored.UsageCompletionTotal;
        session.UsageLastTps = stored.UsageLastTps;
        session.UsageCost = stored.UsageCost;
        RebuildSessionClient(session);
        foreach (var rule in stored.WhitelistedDirectories)
            session.WhitelistedDirectories.Add(new SessionWhitelistEntry(rule.Directory, rule.Tool));
        if (stored.Context.Count > 0)
            session.LockWorkDirectory(); // 已有对话即锁定,与运行期"发送即锁"一致——目录为空的会话跑过一轮同样不可再改
        session.Session.AddMessages(stored.Context);
        foreach (var m in stored.Context) // 旧会话文件无消息 id:加载后补生成,保持"全量消息可寻址"不变量
            if (string.IsNullOrEmpty(m.Id))
                m.Id = Guid.NewGuid().ToString("N");
        foreach (var message in stored.Display)
            RestoreMessage(session, message);
        return session;
    }

    private void RestoreMessage(SessionViewModel session, StoredMessage stored)
    {
        DateTime? time = stored.Time == default ? null : stored.Time;
        if (stored.Role == nameof(MessageRole.User))
        {
            var userMessage = session.AddUser(stored.Text, time);
            userMessage.ClientId = stored.ClientId; // 还原消息标识:/undo 跨重启仍可精确匹配
            foreach (var fileName in stored.ImageFiles) // 从会话图片目录还原用户消息的图片缩略图;文件缺失以占位显示,数量不丢
            {
                var bytes = _store.ReadImage(session.SessionId, fileName);
                userMessage.Images.Add(bytes is null ? PlaceholderImage : DecodeImageBytes(bytes) ?? PlaceholderImage);
                userMessage.ImageFiles.Add(fileName); // 必须回填 VM,否则下次保存会把它从磁盘抹掉
            }
            foreach (var f in stored.TextFiles) // 还原文本文件附件:气泡内文件卡片,点击打开查看器
                userMessage.TextFiles.Add(new TextFileAttachmentViewModel
                {
                    Name = f.Name,
                    SizeText = FormatSize(Encoding.UTF8.GetByteCount(f.Content)),
                    Content = f.Content,
                });
            return;
        }
        if (stored.Role == nameof(MessageRole.Status))
        {
            var status = session.AddStatus(stored.Text, time);
            status.IsError = stored.IsError; // 错误状态行还原:重启后红色不丢
            return;
        }
        if (stored.Role == nameof(MessageRole.Divider))
        {
            // 用落盘的分割线原文(「会话已压缩」/「压缩失败」/「压缩已中断」),旧文件缺文本才回落默认;
            // 时间用原压缩时间,避免还原后默认成"刚刚"(且保存时不再回写失真值)
            session.AddDivider(string.IsNullOrWhiteSpace(stored.Text) ? "会话已压缩" : stored.Text, time: time);
            return;
        }
        if (stored.Role == nameof(MessageRole.Spinner))
            return; // 临时条目本不应落盘,防御性跳过
        var assistant = session.AddAssistant(time);
        assistant.IsBusy = false;
        assistant.RunClientId = stored.RunClientId; // 回合关联还原:重启后 display↔context 关联不丢
        assistant.TurnMessageIds.AddRange(stored.MsgIds); // display↔context 关联还原:重启后 UI 详情取用不丢
        foreach (var step in stored.Steps)
        {
            switch (step.Kind)
            {
                case "content":
                    // 隐藏情绪 Tag:旧记录还原时同样剥离(Display 保持无 tag;Context 原文不受影响)
                    assistant.AppendContent(_agentConfig.HideEmotionTags
                        ? EmotionTagCodec.StripAll(step.Text, _host.EmotionTagStyle)
                        : step.Text);
                    if (assistant.Steps.LastOrDefault() is ContentSegmentViewModel restored)
                        restored.MsgId = step.MsgId; // 步骤关联还原(还原按序进行,最后一段即本步)
                    break;
                case "thinking":
                    var thinking = new ThinkingSegmentViewModel { MsgId = step.MsgId };
                    thinking.Restore(step.Text, step.Seconds ?? 0);
                    assistant.Steps.Add(thinking);
                    break;
                case "tool":
                    var row = new ToolCallViewModel(
                        string.IsNullOrWhiteSpace(step.ToolDisplayName) ? step.ToolName : step.ToolDisplayName,
                        step.ToolName)
                    {
                        Summary = step.Summary,
                        DetailBody = step.DetailBody,
                        ResultSummary = step.ResultSummary,
                        IsRunning = false,
                        IsError = step.IsError, // 失败标记还原:重启后红色状态不丢
                        PathLink = step.PathLink, // 文件名链接还原:重启后仍可点击用默认应用打开
                        PathOpenTarget = step.PathOpenTarget,
                        PathWorkdir = step.PathWorkdir,
                        StatAdd = step.StatAdd, // 行数统计还原:重启后卡片头部 +n -n 仍在
                        StatDel = step.StatDel,
                        SubRunId = step.SubRunId, // 子代理运行关联还原:重启后 sub_* 行仍可点击
                        SubRunTitle = step.SubRunTitle, // 任务名称还原:重启后点击入口仍显示任务名
                        ToolCallId = step.ToolCallId, // 工具调用关联还原:重启后行仍可回链 context 消息
                    };
                    if (step.Diff.Count > 0)
                        row.SetDiff(step.Diff.Select(d => new DiffLineViewModel(d.LineNo, d.Text, d.Kind)).ToList());
                    foreach (var fileName in step.ImageFiles) // 还原工具结果图片(如截图):从会话图片目录读回,缺失以占位显示
                    {
                        var bytes = _store.ReadImage(session.SessionId, fileName);
                        row.AddImage(bytes is null ? PlaceholderImage : DecodeImageBytes(bytes) ?? PlaceholderImage);
                        row.AddImageFile(fileName); // 回填文件名:重启后再保存不丢图(与用户消息图片同规则)
                    }
                    assistant.Steps.Add(row);
                    break;
            }
        }
    }

    /// <summary>自动会话标题:取首条用户消息的第一行(多行提示词的后续行不进标题),
    /// 只留一个宽松上限——标题栏很宽,截太短浪费空间,窄处(侧栏列表/文档标题)由 UI 按宽度自行省略。</summary>
    private static string MakeSessionTitle(string source)
    {
        var t = source.Trim();
        var i = t.IndexOfAny(new[] { '\r', '\n' });
        if (i >= 0) t = t[..i].Trim();
        return t.Length > 40 ? t[..40] + "…" : t;
    }

    /// <summary>把会话写入磁盘(上下文 + 渲染数据)。空对话同样落盘:创建即写入,
    /// 重启后空对话保留在会话列表(用户新建后放弃的也不会消失)。</summary>
    public void SaveSession(SessionViewModel session)
    {
        _store.Save(BuildStored(session));
    }

    private const int SaveThrottleMs = 3000;
    private readonly Dictionary<string, long> _lastThrottledSaveTick = new(StringComparer.Ordinal);

    /// <summary>逐请求落盘(节流):每次 LLM 请求完成都会触发全量 diff 保存,长会话 diff 成本随上下文线性涨。
    /// 按会话 3s 节流——用量统计至多滞后一次请求,轮末 finally 的 SaveSession 强制兜底,
    /// 强杀/崩溃至多丢最近几秒的计数,不丢消息。</summary>
    public void SaveSessionThrottled(SessionViewModel session)
    {
        var now = Environment.TickCount64;
        if (_lastThrottledSaveTick.TryGetValue(session.SessionId, out var last) && now - last < SaveThrottleMs)
            return;
        _lastThrottledSaveTick[session.SessionId] = now;
        SaveSession(session);
    }

    /// <summary>保存全部非流式会话(窗口关闭/退出时调用;流式中的会话由本次运行结束时按轮保存)。</summary>
    public void SaveAllSessions()
    {
        foreach (var session in Sessions)
        {
            if (session.IsStreaming)
                continue;
            SaveSession(session);
        }
    }

    /// <summary>退出前收尾:取消全部运行中的请求并等运行真正结束——
    /// RunAsync 的中断路径(Flush 已完成迭代 + finally 落盘)在 UI 线程续延里执行,
    /// 这里 await 让出调度器即可等到;工具命令可能不响应取消,超时 5 秒兜底。</summary>
    public async Task ShutdownAsync()
    {
        var running = Sessions.Where(s => s.Session.IsRunning).ToList();
        foreach (var s in running)
            s.Session.CancelRun();
        var deadline = Environment.TickCount64 + 5000;
        while (Sessions.Any(s => s.Session.IsRunning) && Environment.TickCount64 < deadline)
            await Task.Delay(50);
        // 兜底恒执行(此前无运行会话时 early-return 跳过):逐请求落盘是第一道,
        // 这里再补一次,进程退出前磁盘状态尽量新
        SaveAllSessions();
    }

    private static StoredSession BuildStored(SessionViewModel vm) => new()
    {
        Id = vm.SessionId,
        Title = vm.Title,
        CreatedAt = vm.CreatedAt,
        CompactedSinceLastSend = vm.CompactedSinceLastSend,
        Archived = vm.IsArchived, // 预览态归档会话被改动落盘时保持归档,不被动"复活"
        Scene = vm.Scene, // 注入的情景随会话落盘(重启后继续生效)
        WorkingDirectory = vm.WorkingDirectory, // 工作目录/权限/信任白名单随会话落盘(重启不丢)
        WorkingDirectoryAccess = vm.WorkingDirectoryAccess,
        ChatProvider = vm.ChatProvider, // 会话级供应商/模型/思考等级随会话落盘(每会话可用不同模型)
        ChatModel = vm.ChatModel,
        ThinkingEffort = vm.ThinkingEffort,
        LastRoundProvider = vm.LastRoundProvider, // 上一轮实际使用的供应商/模型随会话落盘(重启后模型切换分割线仍正确)
        LastRoundModel = vm.LastRoundModel,
        LastPromptTokens = vm.LastPromptTokens ?? 0, // 服务端回报的真实 KV 占用随会话落盘:重启后占用条/压缩判断按真实值还原
        UsagePromptTotal = vm.UsagePromptTotal, // 会话累计用量随会话落盘:状态栏重开继续显示
        UsageCachedTotal = vm.UsageCachedTotal,
        UsageCompletionTotal = vm.UsageCompletionTotal,
        UsageLastTps = vm.UsageLastTps,
        UsageCost = vm.UsageCost,
        WhitelistedDirectories = vm.WhitelistedDirectories
            .Select(w => new StoredDirectoryRule { Directory = w.Directory, Tool = w.Tool })
            .ToList(),
        Context = [.. vm.Session.Messages],
        Display = vm.Messages.Where(m => !m.Ephemeral).Select(ToStoredMessage).ToList(), // 临时条目(压缩齿轮行)不落盘
    };

    private static StoredMessage ToStoredMessage(ChatMessageViewModel m) => new()
    {
        Role = m.Role.ToString(),
        Text = m.Text,
        Time = m.Time,
        ClientId = m.ClientId, // 用户消息标识随 Display 落盘:重启后 /undo 仍可按 Id 匹配 context
        RunClientId = m.RunClientId, // 助手气泡的回合关联(触发用户消息 id)随 Display 落盘
        MsgIds = [.. m.ContextMessageIds()], // display↔context 关联:UI 后期可按 id 取原始消息展示详情
        IsError = m.IsError, // 错误状态(红色显示)随 Display 落盘
        ImageFiles = m.ImageFiles,
        TextFiles = m.TextFiles.Select(f => new StoredTextFile { Name = f.Name, Content = f.Content }).ToList(),
        Steps = m.Steps.Select(ToStoredStep).OfType<StoredStep>().ToList(),
    };

    /// <summary>占位缩略图:WPF 无法预览的格式(如 WebP 无解码器)在气泡与还原时以此显示,
    /// 保证「发送的图片」与「气泡/落盘里的图片」数量一致。</summary>
    internal static readonly ImageSource PlaceholderImage = CreatePlaceholderImage();

    private static ImageSource CreatePlaceholderImage()
    {
        const int size = 72; // 与缩略图显示尺寸一致
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)), null, new Rect(0, 0, size, size));
            var glyph = new FormattedText("\uE91B", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe MDL2 Assets"), 30,
                new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x99)), 1.0);
            dc.DrawText(glyph, new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>把图片原始字节解码为位图(从本地文件还原缩略图用)。</summary>
    private static ImageSource? DecodeImageBytes(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 坏图(WIC 无解码器,如 SVG 伪装的 .png)不崩应用:调用点回退占位图
            return null;
        }
    }

    /// <summary>从 data URL(data:image/...;base64,...)提取原始字节。</summary>
    private static byte[] DecodeDataUrlBytes(string dataUrl) =>
        Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);

    /// <summary>把 data URL 解码为位图(附件无内存预览时打开全分辨率预览用);失败返回 null。</summary>
    internal static ImageSource? DecodeDataUrlImage(string dataUrl)
    {
        try
        {
            return DecodeImageBytes(DecodeDataUrlBytes(dataUrl));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>附件的原始扩展名:文件附件用文件自己的扩展名(落盘保持原样),剪贴板图片统一 PNG;
    /// PDF/音视频附件保持自身扩展名(前端按扩展名区分缩略图与文件卡)。</summary>
    private static string ExtOf(AttachmentViewModel a)
    {
        var ext = Path.GetExtension(a.Name).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
            return ext;
        return ext is ".pdf" or ".mp3" or ".wav" or ".ogg" or ".m4a" or ".mp4" or ".webm" or ".mov" ? ext : ".png";
    }

    private static StoredStep? ToStoredStep(object step) => step switch
    {
        // MsgId:本段渲染自的 context 助手消息 id(运行时由 OnTurnStarted 盖入),落盘后步骤可精确回链
        ContentSegmentViewModel content => new StoredStep { Kind = "content", Text = content.Text, MsgId = content.MsgId },
        ThinkingSegmentViewModel thinking => new StoredStep
        {
            Kind = "thinking",
            Text = thinking.Text,
            Seconds = thinking.Seconds,
            MsgId = thinking.MsgId,
        },
        ToolCallViewModel tool => new StoredStep
        {
            Kind = "tool",
            ToolCallId = tool.ToolCallId, // 与 context 的 tool_calls[].id 互链:行可回链发起/结果消息
            ToolName = tool.ToolName,
            ToolDisplayName = tool.DisplayName,
            Summary = tool.Summary,
            DetailBody = tool.DetailBody,
            ResultSummary = tool.ResultSummary,
            IsError = tool.IsError, // 失败标记随步骤落盘(重启后红色状态不丢)
            ImageFiles = tool.ImageFiles.ToList(), // 工具结果图片(如截图)的文件名随步骤落盘,重启可还原
            PathLink = tool.PathLink, // 文件名链接随步骤落盘:重启后读取/编辑行仍可点击打开
            PathOpenTarget = tool.PathOpenTarget,
            PathWorkdir = tool.PathWorkdir,
            StatAdd = tool.StatAdd, // 行数统计随步骤落盘:重启后卡片头部 +n -n 不丢
            StatDel = tool.StatDel,
            SubRunId = tool.SubRunId, // 子代理运行关联随步骤落盘:重启后 sub_* 行仍可点击
            SubRunTitle = tool.SubRunTitle, // 任务名称随步骤落盘:重启后点击入口仍显示任务名
            Diff = tool.DiffLines.Select(d => new StoredDiffLine
            {
                LineNo = d.LineNo,
                Text = d.Text,
                Kind = d.Kind,
            }).ToList(),
        },
        _ => null, // 鉴权卡片等一次性 UI 不持久化
    };

    // ───────── 归档(右键菜单与归档窗口调用) ─────────

    /// <summary>已归档会话(按更新时间降序,归档窗口展示)。标量查询:不加载消息与气泡内容。</summary>
    public List<StoredSession> ArchivedSessions() =>
        _store.LoadSummaries().Where(s => s.Archived).OrderByDescending(s => s.UpdatedAt).ToList();

    /// <summary>归档会话:打标记写盘并从左侧列表移除(文件保留,可移出归档恢复);绑定的直播间消息会话随归档自动解绑。</summary>
    public void ArchiveSession(SessionViewModel session)
    {
        var stored = BuildStored(session);
        stored.Archived = true;
        _store.Save(stored);
        Sessions.Remove(session);
        _host.Agent.NotifySessionRemoved(session.Session); // 插件清理按会话维护的状态
        if (string.Equals(_host.BiliLive.BoundSessionId, session.SessionId, StringComparison.Ordinal))
            _host.BiliLive.BindSession(null); // 归档即解绑:消息不再投递到归档会话
        if (SelectedSession == session)
            SelectedSession = Sessions.LastOrDefault();
        if (Sessions.Count == 0)
            NewSession();
        WebEventHub.PublishAgent(_agentConfig.Id, "sessions_changed"); // 列表增删属性广播覆盖不到:打开的归档预览视图据此回拉快照翻成只读
    }

    /// <summary>移出归档:取消标记并恢复到左侧列表。
    /// select=false(Web 移出)不改后端选中——前端停在归档面板原地刷新,不因恢复动作跳走;
    /// 桌面归档窗口用默认 true,保持"恢复并选中"的既有交互。</summary>
    public void UnarchiveSession(string id, bool select = true)
    {
        var stored = _store.Load(id);
        if (stored is null)
            return;
        stored.Archived = false;
        _store.Save(stored);
        // 列表布局:槽缺失(旧库归档的会话)补根槽;原文件夹在归档期间已被删除 → 回根组
        var slots = _store.ListSessionSlots();
        if (!slots.TryGetValue(id, out var slot))
            _store.AppendSessionSlot(id, null);
        else if (slot.FolderId is { } fid && !_store.FolderExists(fid))
            _store.MoveSessionToFolder(id, null);
        var existing = Sessions.FirstOrDefault(s => s.SessionId == id);
        if (existing is not null)
        {
            existing.IsArchived = false; // 只读预览实例已在内存:摘掉只读标记即可,不必重建
            if (select)
                SelectedSession = existing;
        }
        else
        {
            var vm = RestoreSession(stored);
            Sessions.Add(vm);
            if (select)
                SelectedSession = vm;
        }
        WebEventHub.PublishAgent(_agentConfig.Id, "sessions_changed"); // 列表增删属性广播覆盖不到:打开的预览视图据此回拉快照恢复可发送
    }

    /// <summary>懒恢复一个归档会话为只读预览实例(Web 归档列表点开某条 → 聊天区回看):
    /// 与普通恢复同法还原上下文/气泡,但打上只读标记——不进会话列表,发送/命令/压缩/撤销由各端点拦截。
    /// 已在内存中的实例直接返回;非归档或会话不存在返回 null(调用方按 404 处理)。</summary>
    public SessionViewModel? RestoreArchivedPreview(string id)
    {
        var existing = Sessions.FirstOrDefault(s => s.SessionId == id);
        if (existing is not null)
            return existing.IsArchived ? existing : null;
        var stored = _store.Load(id);
        if (stored is null || !stored.Archived)
            return null;
        var vm = RestoreSession(stored);
        vm.IsArchived = true;
        Sessions.Add(vm);
        RecyclePreviewInstances();
        return vm;
    }

    /// <summary>回收归档只读预览实例:预览即用即弃,此前永久驻留 Sessions——每开一个归档会话就多一份
    /// 全量上下文/气泡在内存,长期使用只增不减。保留最近若干个(频繁来回翻看不抖动),选中的不回收。</summary>
    private void RecyclePreviewInstances()
    {
        const int maxPreviews = 5;
        var previews = Sessions
            .Where(s => s.IsArchived && s != SelectedSession && !s.Session.IsRunning)
            .ToList();
        for (var i = 0; i < previews.Count - maxPreviews; i++)
            Sessions.Remove(previews[i]);
    }

    /// <summary>永久删除会话文件(归档窗口调用;若会话还在列表中则一并移除)。</summary>
    public void DeleteSessionForever(string id)
    {
        var session = Sessions.FirstOrDefault(s => s.SessionId == id);
        if (session is not null)
            _host.Agent.NotifySessionRemoved(session.Session); // 插件清理按会话维护的状态
        _store.Delete(id);
        if (string.Equals(_host.BiliLive.BoundSessionId, id, StringComparison.Ordinal))
            _host.BiliLive.BindSession(null); // 永久删除的会话若绑定了直播间消息:随删除解绑
        var vm = Sessions.FirstOrDefault(s => s.SessionId == id);
        if (vm is null)
            return;
        Sessions.Remove(vm);
        if (SelectedSession == vm)
            SelectedSession = Sessions.LastOrDefault();
        if (Sessions.Count == 0)
            NewSession();
    }

    /// <summary>清空全部归档:逐一永久删除所有已归档会话(归档窗口「全部清空」调用)。</summary>
    public void ClearAllArchivedSessions()
    {
        foreach (var s in ArchivedSessions())
            DeleteSessionForever(s.Id);
    }

    /// <summary>新消息加入时触发(用于自动滚动),参数为所属会话。</summary>
    public event Action<SessionViewModel, System.Collections.Specialized.NotifyCollectionChangedEventArgs>? MessageAdded;

    /// <summary>鉴权卡片加入聊天流后触发(UI 线程);供宿主挂接“窗体不可见时改弹独立确认窗口”的处理。</summary>
    public Action<AuthRequestViewModel>? AuthPopupRequested { get; set; }

    /// <summary>输入卡片模型下拉的可选模型(来自各供应商的缓存列表,点开下拉不发请求)。</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AvailableModels { get; } = new();

    private int _fetchingModels;

    /// <summary>一次 /models 拉取的结果:模型名列表 + 各模型的上下文大小(服务端提供时)。</summary>
    private sealed class ProviderModels
    {
        public List<string> Names { get; } = new();
        public Dictionary<string, int> Contexts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, ProviderModels> _modelsByProvider = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用当前选中会话的供应商模型清单填充下拉;未选择供应商时留空(模型随供应商,发送已被拦截)。</summary>
    private void ShowCachedModels()
    {
        AvailableModels.Clear();
        var session = SelectedSession;
        if (session is null || session.ChatProvider.Length == 0)
            return;
        foreach (var model in ProviderFor(session).Models)
            AvailableModels.Add(model.Name);
    }

    /// <summary>
    /// 拉取所有在用供应商的模型列表(GET {BaseUrl}/models)并按供应商缓存:
    /// 在用 = 各会话记录的供应商 + Agent 生效供应商;已有缓存的供应商跳过请求。
    /// </summary>
    public async Task RefreshModelsAsync()
    {
        var names = Sessions.Select(s => s.ChatProvider)
            .Append(_agentConfig.Llm.ActiveProvider)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var name in names)
            await FetchProviderModelsAsync(name);
    }

    /// <summary>拉取单个供应商的模型列表并缓存(已有缓存不发请求);失败不缓存以便下次重试。</summary>
    private async Task FetchProviderModelsAsync(string providerName)
    {
        var provider = _agentConfig.Llm.Find(providerName);
        if (provider is null || _modelsByProvider.ContainsKey(provider.Name))
            return;
        if (Interlocked.Exchange(ref _fetchingModels, 1) == 1)
            return;
        try
        {
            var baseUrl = provider.BaseUrl.Trim();
            if (baseUrl.Length == 0)
                return;
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                baseUrl.TrimEnd('/') + "/models");
            if (provider.ApiKey.Length > 0)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var result = new ProviderModels();
            if (doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var entry in arr.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
                        continue;
                    var name = id.GetString()!;
                    result.Names.Add(name);
                    var context = ParseModelContext(entry);
                    if (context.HasValue)
                        result.Contexts[name] = context.Value;
                }

            if (result.Names.Count == 0)
                return;
            _modelsByProvider[provider.Name] = result; // 成功才缓存,失败(如服务端加载中 503)下次重试
            ShowCachedModels();
            RefreshContextUsage();
        }
        catch
        {
            // 拉取失败保留下拉现状(至少含当前模型),不缓存以便下次重试
        }
        finally
        {
            Interlocked.Exchange(ref _fetchingModels, 0);
        }
    }

    /// <summary>输入卡片工具栏的上下文占用:服务端回报的真实 KV 占用 / 生效上限(含百分比)。
    /// 本地估算不可靠,不参与显示(尚无回报时为 0);上限与压缩同一口径(会话所选模型);未选模型不显示。</summary>
    public string ContextUsageText
    {
        get
        {
            var session = SelectedSession;
            if (session is null)
                return "";
            var used = session.LastPromptTokens ?? 0; // 真实占用(每次 LLM 请求后更新,随会话落盘);未回报前为 0
            var limit = ResolveContextLimit(session.Session);
            if (limit <= 0)
                return ""; // 未选模型:不显示占用(显示 ∞ 反而费解)
            return $"{ContextTokens.Format(used)}/{ContextTokens.Format(limit)} ({used * 100.0 / limit:0.#}%)";
        }
    }

    /// <summary>重新计算并通知上下文占用文本。</summary>
    public void RefreshContextUsage() => Raise(nameof(ContextUsageText));

    /// <summary>API /v1/models 为指定供应商+模型上报的上下文上限(按供应商缓存);未提供返回 0。</summary>
    private int ResolveApiContext(string providerName, string model)
    {
        return providerName.Length > 0 && model.Length > 0
            && _modelsByProvider.TryGetValue(providerName, out var cached)
            && cached.Contexts.TryGetValue(model, out var ctx) ? ctx : 0;
    }

    /// <summary>
    /// 从 /v1/models 条目解析上下文上限,兼容各家字段:max_context_tokens / max_model_len /
    /// context_length / max_input_tokens;llama.cpp 的 meta.n_ctx(回退 n_ctx_train)。
    /// </summary>
    private static int? ParseModelContext(System.Text.Json.JsonElement entry)
    {
        foreach (var name in new[] { "max_context_tokens", "max_model_len", "context_length", "max_input_tokens" })
            if (entry.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.GetInt32() > 0)
                return v.GetInt32();
        foreach (var section in new[] { "meta", "model_info" })
            if (entry.TryGetProperty(section, out var si) && si.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var name in new[] { "n_ctx", "n_ctx_train" })
                    if (si.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.GetInt32() > 0)
                        return v.GetInt32();
        return null;
    }

    /// <summary>工具协议名 → 可读显示名(Definition.DisplayName,未设置时用协议名)。
    /// 查 GetAllTools 而非 GetRegisteredTools:插件动态工具(如 skill)也带中文显示名。</summary>
    private string ResolveToolDisplayName(string toolName) =>
        _host.Agent.Tools.GetAllTools()
            .FirstOrDefault(t => t.Definition.Name == toolName)?.Definition.GetDisplayName() ?? toolName;

    /// <summary>气泡是否为空(无正文/思考/步骤),用于移除占位气泡。
    /// 全部是空白正文段也算空:隐藏情绪 Tag 后,纯 tag 回复(如只输出 [happy])剥离完即无可见内容。</summary>
    private static bool IsEmpty(ChatMessageViewModel message) =>
        message.Steps.Count == 0 ||
        message.Steps.All(s => s is ContentSegmentViewModel { Text: { } text } && text.Trim().Length == 0);

    /// <summary>移除尾部连续的助手空气泡(原始占位,或运行中压缩翻页后未承接内容的新气泡)。</summary>
    private static void TrimEmptyTailBubbles(SessionViewModel session)
    {
        while (session.Messages.Count > 0
               && session.Messages[^1] is { Role: MessageRole.Assistant } last
               && IsEmpty(last))
            session.Messages.RemoveAt(session.Messages.Count - 1);
    }

    /// <summary>发送一条输入(可携带待发送附件);[bili-live] 触发串不渲染用户气泡(会被插件移除),也不消费附件。
    /// target 为空时发到当前选中会话;直播间轮次由宿主显式传入绑定的会话。
    /// attachments 为空时消费目标会话的草稿(桌面路径);Web 传入随请求收集的附件——
    /// 此前经 VM 级 Attachments(= SelectedSession 的草稿)中转,Web 切会话不改后端选中,
    /// 多标签/多窗口场景附件会挂错会话,桌面正在编辑的草稿还会被误发/误清。
    /// 流式锁按会话独立:一个会话在跑不影响其他会话发送;桌宠开启的 Agent 内多会话经 FIFO 串行(见 RunTurnAsync)。</summary>
    public async Task SendAsync(string input, SessionViewModel? target = null,
        IReadOnlyList<AttachmentViewModel>? attachments = null)
    {
        var session = target ?? SelectedSession;
        if (session is null || session.IsStreaming || session.Session.IsRunning)
            return; // 仅锁本会话;触发串在忙时直接丢弃(事件仍在插件队列,等下次触发),不打进 FIFO 污染上下文

        var pending = attachments ?? session.DraftAttachments;
        var text = (input ?? "").Trim();
        var isTrigger = input == BiliLivePlugin.TriggerInput;
        if (isTrigger ? text.Length == 0 : (text.Length == 0 && pending.Count == 0))
            return;

        // 气泡只显示用户原文 + 文件卡片;发给模型时用户原文与每个文本文件各为独立 text 片段(结构隔离,任意模型可用);图片以多模态内容发送
        var body = text;
        var imageUrls = new List<string>();
        var fileParts = new List<string>();
        string? clientId = null; // 宿主侧消息标识:仅真实用户消息生成(触发串无 display 气泡,不需要)
        if (!isTrigger)
        {
            var textFiles = pending.Where(a => a.Kind == AttachmentKind.TextFile).ToList();
            foreach (var file in textFiles) // 每文件独立成段:模型可清晰区分用户原话与各文件内容的边界
                fileParts.Add("[Attached file: " + file.Name + "]\n" + file.TextContent);
            imageUrls.AddRange(pending
                .Where(a => a.Kind == AttachmentKind.Image && a.DataUrl is not null)
                .Select(a => a.DataUrl!));

            // 图片写入该会话的图片目录(内容哈希 + 原始扩展名,会话内同内容复用),消息只存文件名;WPF 解不了的格式用占位缩略图
            var previews = new List<(ImageSource, string FileName)>();
            foreach (var a in pending.Where(a => a.Kind == AttachmentKind.Image && a.DataUrl is not null))
                previews.Add((a.Preview ?? PlaceholderImage, _store.StoreImage(session.SessionId, DecodeDataUrlBytes(a.DataUrl!), ExtOf(a))));
            // display 气泡与本轮写回 context 的用户消息共用同一 GUID(/undo 据此精确匹配,不发给模型)
            clientId = Guid.NewGuid().ToString("N");
            var userMessage = previews.Count > 0 ? session.AddUserWithImages(text, previews) : session.AddUser(text);
            userMessage.ClientId = clientId;
            session.CompactedSinceLastSend = false; // 已发送新消息:解除压缩后的撤销封锁(即使本轮失败,回滚点仍成立)
            foreach (var f in textFiles) // 气泡内文件卡片;内容随消息落盘,点击打开文本查看器
                userMessage.TextFiles.Add(new TextFileAttachmentViewModel
                {
                    Name = f.Name,
                    SizeText = f.SizeText,
                    Content = f.TextContent ?? "",
                });

            var titleSource = text.Length > 0 ? text : imageUrls.Count > 0 ? "图片" : textFiles.Count > 0 ? textFiles[0].Name : "";
            if (session.Title == "新对话" && titleSource.Length > 0)
                session.Title = MakeSessionTitle(titleSource);

            if (attachments is null)
                ClearAttachments(); // 桌面路径:已组装进本条消息,清空缩略图行(Attachments 即选中会话的草稿)
        }

        session.LockWorkDirectory(); // 首次对话后工作目录锁定

        // 占位气泡:回合的步骤容器(流式步骤/回链 id 都挂它);空壳在 Web 侧不渲染,
        // 发送按钮为中断、本会话不可再发送(门禁看 IsStreaming);可见运行指示 = 输入框圆盘左侧齿轮
        var assistant = session.AddAssistant();
        assistant.RunClientId = clientId; // 回合关联:该气泡渲染的 context 消息区间从此用户消息起(触发串无 clientId,留空)
        session.IsStreaming = true;

        // 桌宠开启的 Agent:TTS 音频与角色头顶气泡全局唯一,两个会话同时出声/冒泡会互相打断,
        // 同 Agent 内多会话串行执行,后到的请求进 FIFO,当前轮结束后自动接续;
        // 桌宠关闭(TTS 与气泡随之失效)时会话完全并发,互不阻塞
        if (_host.IsPetEnabled && Sessions.Any(s => s.Session.IsRunning))
        {
            if (isTrigger)
            {
                // 触发串忙时直接丢弃(事件仍在插件队列,等下次触发),不入队以免空轮污染上下文:撤掉占位
                session.Messages.Remove(assistant);
                session.IsStreaming = false;
                return;
            }
            session.PendingAssistant = assistant; // 排队占位:取消时按引用清理
            _pendingTurns.Enqueue(() => RunTurnAsync(session, body, imageUrls, fileParts, clientId, isTrigger, assistant));
            return;
        }

        await RunTurnAsync(session, body, imageUrls, fileParts, clientId, isTrigger, assistant);
    }

    /// <summary>会话的有效工作目录;无目录时在临时工作区根下按会话建一个并回填会话。
    /// 没有这层兜底,命令/文件工具的 cwd 会落到应用安装目录(部署盘根),既奇怪又危险。
    /// 根目录取安全设置 WorkspaceRoot,空 = %LOCALAPPDATA%\APet\workspaces;子目录 ws-&lt;会话短Id&gt;
    /// 重复运行/重启后复用。创建失败返回现值(null:保持无目录,工具退回进程目录),不阻塞对话。</summary>
    private string? EnsureSessionWorkspace(SessionViewModel session)
    {
        if (!string.IsNullOrWhiteSpace(session.WorkingDirectory))
            return session.WorkingDirectory;
        try
        {
            var root = _agentConfig.Security.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APet", "workspaces");
            var id = session.SessionId;
            var dir = Path.Combine(root.Trim(), "ws-" + id[..Math.Min(8, id.Length)]);
            Directory.CreateDirectory(dir); // 已存在则复用:重启/重复运行同一会话共享工作区
            _dispatcher.Invoke(() =>
            {
                if (string.IsNullOrWhiteSpace(session.WorkingDirectory)) // 双开窗口竞态:后到者沿用先到者
                {
                    session.WorkingDirectory = dir;
                    if (string.IsNullOrWhiteSpace(session.WorkingDirectoryAccess))
                        session.WorkingDirectoryAccess = "ReadWrite";
                    SaveSession(session); // 立即落盘:重启后同一会话仍指向该工作区
                }
            });
            return session.WorkingDirectory;
        }
        catch (Exception ex)
        {
            Log.Global.Error("[Workspace] 会话临时工作区创建失败: " + ex.Message);
            return session.WorkingDirectory;
        }
    }

    /// <summary>执行一轮完整对话(RunAsync + 收尾);立即执行或从桌宠 FIFO 出队时调用。
    /// 所有异常就地处理不外抛;结束时落盘并泵出排队的下一轮。</summary>
    private async Task RunTurnAsync(SessionViewModel session, string body,
        List<string> imageUrls, List<string> fileParts, string? clientId, bool isTrigger, ChatMessageViewModel assistant,
        List<QueuedUserMessage>? preQueued = null)
    {
        session.PendingAssistant = null; // 已出队
        if (!Sessions.Contains(session) || session.PendingTurnCancelled)
        {
            session.PendingTurnCancelled = false;
            PumpPendingTurns(); // 本轮已丢弃(排队期间被归档/删除/取消,气泡已由取消路径清理):继续放出后续
            return;
        }

        session.IsStreaming = true; // 立即路径在此置位;排队路径入队时已置位(幂等)
        // 本轮实际使用的供应商/模型 = 轮次起始的会话选择(轮内切换被会话锁挂起,连接不变、下轮生效)。
        // 计费按此锚定:usage 回报时会话"当前选择"可能已被用户切走,照它计价会错位到错误模型的价格。
        var billingAnchor = (Provider: session.ChatProvider, Model: session.ChatModel);
        // 模型切换分割线:本轮生效模型与上一轮不一致(用户上一轮期间/结束后切了模型)→
        // 在本轮用户消息上方插一行(与压缩分割线同款;触发串轮次无用户消息,落在本轮之前)
        if (session.LastRoundModel.Length > 0
            && (session.LastRoundProvider, session.LastRoundModel) != (billingAnchor.Provider, billingAnchor.Model))
            session.AddDivider($"模型由 {LlmLabel(session.LastRoundProvider, session.LastRoundModel)} 切换为 {LlmLabel(billingAnchor.Provider, billingAnchor.Model)}",
                CompressionInsertIndex(session));
        session.LastRoundProvider = billingAnchor.Provider;
        session.LastRoundModel = billingAnchor.Model;
        UiCallbacks? callbacks = null; // finally 中断兜底(收口卡在"运行中"的工具行)需用
        var runSucceeded = false; // 仅成功收尾才自动派发剩余队列(中断/出错:队列清空,见 finally)
        try
        {
            // 会话级安全策略:基于 Agent 的其他目录/白名单设定,套用本会话的工作目录与权限
            var policy = UiConfig.BuildSecurity(_agentConfig.Security);
            policy.SessionId = session.SessionId; // 会话标识随执行环境注入:TODO/后台任务按会话隔离
            if (isTrigger)
            {
                // B站插件触发的轮次:直播间消息可能诱导危险操作,只放行白名单内的工具。
                // 白名单在工具调用时统一拦截,不改动请求中的工具定义,保证提示词缓存命中;空白名单 = 不限制
                var whitelist = _agentConfig.BiliLive.ToolWhitelist;
                if (whitelist is { Count: > 0 })
                    policy.AllowedTools = new HashSet<string>(whitelist, StringComparer.Ordinal);
            }
            policy.WorkingDirectory = EnsureSessionWorkspace(session);
            policy.WorkingDirectoryAccess = Enum.TryParse<Framework.Security.DirectoryAccess>(
                session.WorkingDirectoryAccess, ignoreCase: true, out var access)
                ? access
                : Framework.Security.DirectoryAccess.ReadWrite;
            foreach (var entry in session.WhitelistedDirectories) // 会话内"信任此目录"积累的白名单(按工具类别)
                policy.AddWhitelistedDirectory(entry.Directory, entry.Tool);
            _host.ActivePolicies[session.SessionId] = policy; // 子代理(sub_run)按会话继承本策略;finally 清除
            callbacks = new UiCallbacks(_dispatcher, session, assistant, ResolveToolDisplayName, policy, _store, () => _agentConfig)
            {
                // 计费锚定:本轮 usage 按轮次起始的供应商/模型计价,不随轮内切换漂移
                BillingAnchor = billingAnchor,
                // 鉴权卡片加入后通知宿主;Web 形态经事件中枢登记,浏览器经 REST 给出结论
                AuthorizationPopupRequested = card =>
                {
                    AuthPopupRequested?.Invoke(card);
                    WebEventHub.RegisterAuth(_agentConfig.Id, session.SessionId, card);
                },
                // 隐藏情绪 Tag(默认开):流式按正文段剥离,Display 落盘即不含 tag;Context 照常记录原文
                EmotionTagStripping = _agentConfig.HideEmotionTags
                    ? () => new EmotionTagStream(_host.EmotionTagStyle)
                    : null,
                // 运行中压缩翻页:当前气泡已有内容则定格在分割线之前,后续内容落进新气泡;空气泡(发送前压缩)不翻。
                // 翻页即定格:旧气泡的"工作中"齿轮同步熄灭(内容已定稿,后续由新气泡承接),不再等到 run 结束兜底清理
                AssistantBubbleRoll = current =>
                {
                    if (IsEmpty(current)) return null;
                    current.IsBusy = false;
                    return session.AddAssistant();
                },
                // 每次 LLM 请求完成即落盘:长任务中断/崩溃时已完成轮次不丢
                RequestSessionSave = SaveSessionThrottled,
                // 中断兜底:主轮停止后据子运行真实状态收口卡在"运行中"的 sub_* 行(见 FinalizeInterruptedToolRows)
                SubRuns = _host.SubAgent.Manager,
                InterruptedToolRowFinalized = row =>
                {
                    // Web 同步:浏览器还停在"运行中"的工具行补发一帧 tool_finished。
                    // toolCallId 必带:前端按 id 严格匹配,同名并发调用靠名字会错配到别的行
                    WebEventHub.Publish(_agentConfig.Id, session.SessionId, "tool_finished", new
                    {
                        toolName = row.ToolName,
                        toolCallId = row.ToolCallId,
                        displayName = row.DisplayName,
                        summary = row.Summary,
                        detail = row.DetailBody,
                        hasDetail = row.HasDetail,
                        resultSummary = row.ResultSummary,
                        isError = row.IsError,
                        pathLink = row.PathLink,
                        statAdd = row.StatAdd,
                        statDel = row.StatDel,
                        diffLines = row.DiffLines.Select(d => new { lineNo = d.LineNo, text = d.Text, kind = d.Kind }).ToList(),
                        subRunId = row.SubRunId,
                        subRunTitle = row.SubRunTitle,
                    });
                },
            };
            session.CompressionBubbleRoll = () => callbacks.RollAssistantBubble();
            // Web 流式:主回调驱动展示 VM 与持久化,转发链把同一批事件推给会话事件总线(SSE)
            var pipeline = new CompositeCallbacks(callbacks,
                new SseRelay(_agentConfig.Id, session.SessionId, ResolveToolDisplayName, session.WorkingDirectory,
                    // 计费锚定与 UiCallbacks 同源:按本轮实际使用的供应商/模型
                    costResolver: u => Services.LlmUsageCost.Of(_agentConfig, billingAnchor.Provider, billingAnchor.Model, u))
                {
                    // sub_wait/sub_kill 行运行期间回填任务名(与 WPF 回调同口径)
                    RunTitleResolver = id => _host.SubAgent.Manager.Find(id, session.SessionId)?.Title,
                });
            await _host.Agent.RunAsync(session.Session, body, pipeline, policy,
                imageAttachments: imageUrls.Count > 0 ? imageUrls : null,
                extraTextParts: fileParts.Count > 0 ? fileParts : null,
                clientId: clientId,
                preQueuedMessages: preQueued);
            runSucceeded = true;
            // 静默轮/压缩翻页清理:移除尾部连续的空气泡(原始占位气泡,或翻页后未承接内容的气泡),也不计未读
            TrimEmptyTailBubbles(session);
            if (!IsEmpty(assistant) && !IsViewing(session))
                session.Unread = true; // 不在本会话视角(Web 上报的 last-session)收到回复:标记未读(蓝点)
            session.HasError = false; // 本轮成功:清除此前的出错标记(红点)
        }
        catch (OperationCanceledException)
        {
            // 用户中断:不算错误,状态行提示;已有部分文本的保留气泡
            RemoveCompressSpinner(session, "压缩已中断"); // 中断发生在自动压缩阶段:齿轮行原位收口为「压缩已中断」,与失败区分
            session.AddStatus("已中断");
            TrimEmptyTailBubbles(session); // 翻页后本地 assistant 引用已过期,统一按尾部空泡清理
        }
        catch (Exception)
        {
            if (!IsViewing(session))
                session.HasError = true; // 列表红点:该会话最近一次运行出错(正在看时不点点,行内错误状态行已足够)
            TrimEmptyTailBubbles(session); // 没有任何有效产出:撤掉占位气泡(翻页后引用已过期,同样按尾部清理)
            // 错误只展示为一行红色状态、不进 context:框架保证 RunAsync 的所有异常都先经
            // AgentHost.OnErrorAsync 发出"✗ ..."红色状态行(含工具轮内失败),这里不再补发以免重复
        }
        finally
        {
            // 中断/出错兜底:卡在"运行中"的工具行(如 sub_wait 正阻塞)按真实状态收口,
            // 随后落盘与 turn_ended 快照得到的都是终态;子运行本身已由主轮停止联动取消
            callbacks?.FinalizeInterruptedToolRows();
            RemoveCompressSpinner(session, "压缩失败"); // 兜底:压缩过程异常时把齿轮行收口为「压缩失败」(总结为空已由失败事件先行收口;中断已在 catch 收口,此处齿轮行已不在)
            // 运行中压缩翻页后 callbacks 的 _assistant 已换新,本地 assistant 指向的是旧气泡:
            // 必须清掉本会话所有运行中气泡的 busy 态,否则翻页后的新气泡永远停在运行态
            foreach (var m in session.Messages)
                if (m.Role == MessageRole.Assistant && m.IsBusy)
                {
                    m.EndThinking(); // 中断等未走正文回调的场景也要停止“思考中”计时
                    m.IsBusy = false;
                }
            session.IsStreaming = false;
            if (Sessions.Contains(session))
                SaveSession(session); // 每轮运行结束即落盘(上下文 + 气泡渲染数据);
                                      // 会话已在本轮运行期间被删除/归档移出列表时跳过——
                                      // 落盘会按 BuildStored(archived=0) 把行重插回去(僵尸会话复活)
            else
                Log.Global.Info($"[SaveSession] skip: session {session.SessionId} removed from list during run");
            PumpPendingTurns();   // 本轮结束:放出 FIFO 中排队的下一轮(桌宠串行模式)
            _host.ActivePolicies.TryRemove(session.SessionId, out _); // 轮次结束:子代理不再继承本策略
            // Web:无论成功/出错/中断都以 turn_ended 收口,浏览器据此刷新快照对齐最终状态
            WebEventHub.Publish(_agentConfig.Id, session.SessionId, "turn_ended");
            // 成功收尾且队列仍有存量(最后一轮请求之后才入队的插话)→ 自动派发为新一轮,循环直到清空;
            // 中断(手动/出错)不派发——队列整体清空丢弃(停止即放弃本轮所有未发出的插话)。派发自身失败只记日志,不影响收口
            if (runSucceeded && session.Session.QueuedMessages.Count > 0)
                _ = DispatchQueuedTurnAsync(session).ContinueWith(
                    t => Log.Global.Error("中途队列派发失败", t.Exception?.GetBaseException()),
                    TaskContinuationOptions.OnlyOnFaulted);
            else
                session.Session.ClearQueue(); // 中断/出错终止:插话队列全部清空,不再随下次发送带出
        }
    }

    /// <summary>斜杠/插件命令执行核心(/new /summary /compact /undo /fork + $ 插件指令)。
    /// Web 端点与中途队列派发共用:端点把 DTO 直接回给前端 runCommand;派发路径经 command_executed 帧
    /// 让前端做同一套收尾。handled=false = 未识别(调用方回落为普通消息)。</summary>
    public async Task<CommandResultDto> ExecuteChatCommandAsync(SessionViewModel session, string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0 || (trimmed[0] is not ('/' or '$')))
            return new CommandResultDto(false, null, null, null, null);
        var firstToken = trimmed.Split(' ', '\t')[0];
        var arg = trimmed.Length > firstToken.Length ? trimmed[firstToken.Length..].Trim() : "";

        if (trimmed[0] == '$')
        {
            var command = _host.Agent.Plugins.EnabledPlugins
                .SelectMany(r => r.Plugin.GetChatCommands())
                .FirstOrDefault(c => string.Equals(c.Name, firstToken[1..], StringComparison.OrdinalIgnoreCase));
            var feedback = command?.Execute(session.SessionId, arg) ?? "命令不可用(所属插件未启用)";
            if (feedback.Length > 0)
                session.AddStatus(feedback);
            return new CommandResultDto(true, feedback, null, null, null);
        }

        switch (firstToken.ToLowerInvariant())
        {
            case "/new":
                var created = NewSession();
                return new CommandResultDto(true, null, null, created.SessionId, null);
            case "/summary":
                var summary = ContextCompressionPlugin.GetSummaryText(session.Session);
                return new CommandResultDto(true,
                    string.IsNullOrWhiteSpace(summary) ? "本会话还没有摘要(尚未触发过压缩,或使用的是截断策略)" : summary,
                    null, null, null);
            case "/compact":
                if (session.IsStreaming || session.Session.IsRunning)
                    return new CommandResultDto(true, "会话正在运行中,无法压缩", null, null, null);
                var compactReason = await CompactSession(session, arg.Length > 0 ? arg : null);
                // 成功/失败的结果都落分割线(刷新快照可见);仅"无可压缩"没有落位内容,用文字反馈。
                // 中断/失败给空反馈:聊天记录已有「压缩已中断」/「压缩失败」行,再 toast「压缩完成」会自相矛盾
                return new CommandResultDto(true, compactReason switch
                {
                    "empty" => "没有可压缩的旧消息(会话太短或上下文上限未知)",
                    "failed" => null,
                    "cancelled" => null,
                    _ => "压缩完成",
                }, null, null, null);
            case "/undo":
                var restored = UndoSession(session);
                return new CommandResultDto(true, null, restored, null, null);
            case "/fork":
                var forked = ForkSession(session, arg.Length > 0 ? arg : null);
                return new CommandResultDto(true, null, null, null, forked?.SessionId);
            default:
                // /xxx 回落:插件指令以 $ 为原生前缀,用 / 输入同样按命令触发(与"队列命令必触发"同口径);
                // 没有匹配的启用插件指令才回落普通消息。插件异常不外传(回落普通消息)
                try
                {
                    var name = firstToken[1..];
                    var command = _host.Agent.Plugins.EnabledPlugins
                        .SelectMany(r => r.Plugin.GetChatCommands())
                        .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (command is null)
                        return new CommandResultDto(false, null, null, null, null);
                    var feedback = command.Execute(session.SessionId, arg) ?? "";
                    if (feedback.Length > 0)
                        session.AddStatus(feedback);
                    return new CommandResultDto(true, feedback, null, null, null);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"plugin command fallback failed: {ex}");
                    return new CommandResultDto(false, null, null, null, null);
                }
        }
    }

    /// <summary>把中途队列存量按 FIFO 消费:队首是命令样条目就立即按命令触发(与空闲态手动触发同一核心,
    /// 结果经 command_executed 帧交前端做与 runCommand 相同的收尾——toast/摘要模态/新会话跳转/刷新);
    /// 命令之前的普通消息先作一轮发出(最后一条作输入,其余作显式前置),其后的"命令+剩余存量"回队,
    /// 等本轮收尾的自动派发接续消费,维持 FIFO;未识别的 /xxx 回落为普通消息(同空闲态 handled=false 回落)。
    /// 气泡先上屏,气泡/队列条/运行态经一帧 queue_changed 载荷下发(前端就地补丁);随后进入标准 RunTurnAsync 管线
    /// (其收尾再次检查队列,直至清空)。</summary>
    private async Task DispatchQueuedTurnAsync(SessionViewModel session)
    {
        while (true)
        {
            if (!Sessions.Contains(session) || session.IsStreaming || session.Session.IsRunning)
                return; // 已在跑/桌宠 FIFO 已先行:由那一轮的收尾继续处理队列
            var batch = session.Session.DrainQueued();
            if (batch.Count == 0)
                return;

            var searchFrom = 0;
            if (batch[0].IsCommandLike)
            {
                CommandResultDto result;
                try
                {
                    result = await ExecuteChatCommandAsync(session, batch[0].Text);
                }
                catch (Exception ex)
                {
                    Log.Global.Error("中途队列:命令执行失败", ex);
                    session.AddStatus("⚠ 命令执行失败:" + ex.Message);
                    result = new CommandResultDto(true, null, null, null, null);
                }
                if (result.Handled)
                {
                    if (batch.Count > 1)
                        session.Session.RequeueFront(batch.GetRange(1, batch.Count - 1));
                    // 与 runCommand 同源的收尾交前端(toast/摘要模态/跳转);随后 refetch 刷新状态行与队列条
                    WebEventHub.Publish(_agentConfig.Id, session.SessionId, "command_executed", new
                    {
                        text = batch[0].Text,
                        handled = result.Handled,
                        feedback = result.Feedback,
                        restored = result.Restored,
                        newSessionId = result.NewSessionId,
                        forkedId = result.ForkedId,
                    });
                    continue; // 队首命令已消费:接着处理回队后的剩余存量
                }
                searchFrom = 1; // 未识别命令回落为普通消息:本条并入下方消息段,分界从下一条找
            }

            var split = batch.FindIndex(searchFrom, m => m.IsCommandLike);
            if (split < 0)
                split = batch.Count;
            var head = batch.GetRange(0, split); // 非空:batch 至少 1 条,split 至少 1
            var tail = batch.GetRange(split, batch.Count - split); // 空,或以命令样条目开头
            if (tail.Count > 0)
                session.Session.RequeueFront(tail); // 先回队再开跑:中断/出错时存量留在队列,收尾按规则续处理

            var pre = head.Count > 1 ? head.GetRange(0, head.Count - 1) : new List<QueuedUserMessage>();
            var input = head[^1];
            var bubbles = new List<ChatMessageViewModel>(pre.Count + 1);
            foreach (var item in pre)
            {
                var b = CreateQueuedUserBubble(session, item);
                session.Messages.Add(b); // 气泡只构造不入列:派发场景按序追加到尾部
                bubbles.Add(b);
            }
            var lastBubble = CreateQueuedUserBubble(session, input);
            session.Messages.Add(lastBubble);
            bubbles.Add(lastBubble);
            var titleSource = input.Text.Length > 0 ? input.Text
                : input.Images is { Count: > 0 } ? "图片"
                : input.TextFiles is { Count: > 0 } ? input.TextFiles[0].Name : "";
            if (session.Title == "新对话" && titleSource.Length > 0)
                session.Title = MakeSessionTitle(titleSource);
            session.CompactedSinceLastSend = false; // 派发即新一轮用户消息:解除压缩后的撤销封锁
            session.LockWorkDirectory();
            var assistant = session.AddAssistant();
            assistant.RunClientId = pre.Count > 0 ? pre[0].Id : input.Id; // 回合区间锚到本轮第一条消息
            session.IsStreaming = true;
            // 一帧带齐:清空的队列条 + 追加的插话气泡 + 运行态——前端就地补丁,不整树刷新(见 AttachSession 注释)
            WebEventHub.Publish(_agentConfig.Id, session.SessionId, "queue_changed", new
            {
                queuedMessages = SessionSnapshot.QueuedMessagesOf(session),
                insert = new { mode = "append", items = bubbles.Select(m => SessionSnapshot.Message(m)).ToList() },
                streaming = true,
            });
            var imageUrls = input.Images?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            var fileParts = input.TextFiles?.Select(f => "[Attached file: " + f.Name + "]\n" + f.Content).ToList() ?? [];
            await RunTurnAsync(session, input.Text, imageUrls, fileParts, input.Id, isTrigger: false, assistant, pre);
            return; // 消息轮已交给 RunTurnAsync:其收尾的自动派发接着消费回队的剩余存量(勿再入本循环,防双头派发)
        }
    }

    /// <summary>放出排队轮次:桌宠模式一次只放一个(其 finally 结束时再次泵,天然串行);
    /// 桌宠已关闭时把积压请求全部并发放行。</summary>
    private void PumpPendingTurns()
    {
        var petMode = _host.IsPetEnabled;
        while (_pendingTurns.Count > 0 && (!petMode || !Sessions.Any(s => s.Session.IsRunning)))
        {
            var next = _pendingTurns.Dequeue();
            _ = next();
            if (petMode)
                return;
        }
    }

    /// <summary>压缩指定会话(Web 与聊天 /compact 共用;选中版即本方法的转接)。
    /// 返回 "empty" = 没有可压缩的旧消息(会话太短/上下文上限未知/保留窗口已覆盖全部,宿主改用 toast 提示);
    /// "cancelled" = 用户中途点了中断(聊天记录落「压缩已中断」分割线,宿主不得报成功);
    /// "failed" = 总结未产出/过程异常(聊天记录落「压缩失败」分割线,宿主不得报成功);
    /// null = 成功(「会话已压缩」分割线由插件事件落位)。</summary>
    public async Task<string?> CompactSession(SessionViewModel session, string? extra)
    {
        if (session.IsStreaming || session.Session.IsRunning)
            return null;

        session.IsStreaming = true;
        // 预置 Failed:异常路径由 finally 落「压缩失败」行,不应再报"无可压缩"
        var outcome = APet.Plugins.ContextCompression.ContextCompressionPlugin.CompressOutcome.Failed;
        var interrupted = false;
        try
        {
            outcome = await _host.ContextCompression.CompressNowAsync(_host.Agent, session.Session, extra);
            // 手动压缩(无活动 RunAsync 回调链):总结用量直接记账,finally 的 SaveSession 持久化,
            // turn_ended 事件触发 Web 端重取快照,状态栏同步更新
            if (_host.ContextCompression.LastCompressionUsage is not null)
            {
                APet.UI.Services.UsageAccounting.Apply(session, _agentConfig, _host.ContextCompression.LastCompressionUsage);
                _host.ContextCompression.LastCompressionUsage = null;
            }
            // 成功/失败(总结为空)均由插件的开始/完成/失败事件落位分割线
        }
        catch (OperationCanceledException)
        {
            // 用户点了中断:finally 按 interrupted 落「压缩已中断」行,与「压缩失败」区分
            interrupted = true;
        }
        catch (Exception)
        {
            // 压缩过程异常:finally 的兜底把齿轮行收口为「压缩失败」
        }
        finally
        {
            RemoveCompressSpinner(session, interrupted ? "压缩已中断" : "压缩失败"); // 兜底:任何退出路径都不残留齿轮行
            // 兜底:任何退出路径都不残留停在运行态的空气泡(正常轮次在 RunTurnAsync 收口)
            foreach (var m in session.Messages)
                if (m.Role == MessageRole.Assistant && m.IsBusy)
                {
                    m.EndThinking();
                    m.IsBusy = false;
                }
            TrimEmptyTailBubbles(session); // 翻页/占位产生的尾部空气泡一并撤掉
            session.IsStreaming = false;
            SaveSession(session); // 压缩后上下文已是压缩态,立即持久化
            WebEventHub.Publish(_agentConfig.Id, session.SessionId, "turn_ended"); // Web:齿轮行移除/分割线落位,刷新快照对齐
        }
        // 结果四分:无可压缩 / 中断 / 失败 / 成功——中断与失败各落各的分割线,均不得再报"压缩完成"
        if (interrupted)
            return "cancelled";
        if (outcome == APet.Plugins.ContextCompression.ContextCompressionPlugin.CompressOutcome.NothingToCompress)
            return "empty";
        if (outcome == APet.Plugins.ContextCompression.ContextCompressionPlugin.CompressOutcome.Failed)
            return "failed";
        return null;
    }

    /// <summary>撤销指定会话的最后一条用户消息(Web 与聊天 /undo 共用)。</summary>
    public string? UndoSession(SessionViewModel session)
    {
        if (session.IsStreaming || session.Session.IsRunning)
        {
            session.AddStatus("⏳ 正在回复中,暂时无法撤销");
            return null;
        }

        // 显示中的最后一条用户消息(临时条目如压缩齿轮行不参与)
        var target = session.Messages.LastOrDefault(m => m.Role == MessageRole.User && !m.Ephemeral);
        if (target is null)
        {
            session.AddStatus("没有可撤销的用户消息");
            return null;
        }

        if (session.CompactedSinceLastSend)
        {
            session.AddStatus("⚠ 无法撤销:该消息之后发生过上下文压缩(压缩不可撤销),请先发送一条新消息再撤销");
            return null;
        }

        // 定位上下文对应条目:运行成功时 user+assistant 成对写回。
        // 优先按宿主标识精确匹配(发送时 display 气泡与 context 用户消息盖同一 GUID,不受重复文本/纯图片影响);
        // 标识未命中时回退文本匹配——既覆盖纯图片/纯文件消息(正文为空,按"空正文 + 有内容片段"),
        // 也兜底旧会话文件落盘时曾丢失 context 的 client_id(显示侧仍保留 Id,标识匹配必然落空)。
        var text = target.Text ?? "";
        var clientId = target.ClientId;
        var context = session.Session.Messages;
        var index = -1;
        for (var i = context.Count - 1; i >= 0; i--)
        {
            if (context[i].Role != ChatRole.User) continue;
            var hit =
                (clientId is { Length: > 0 } id && context[i].ClientId == id) ||
                (text.Length > 0
                    ? (context[i].Content ?? "") == text
                    : string.IsNullOrEmpty(context[i].Content) && context[i].ContentParts is { Count: > 0 });
            if (hit)
            {
                index = i;
                break;
            }
        }

        if (index < 0 && ContextCompressionPlugin.GetSummaryText(session.Session) is not null)
        {
            // 消息已不在上下文且历史头部有压缩摘要 → 已被压缩出当前上下文范围,无法回滚
            session.AddStatus("⚠ 无法撤销:该消息已超出当前上下文范围(被压缩),无法回滚");
            return null;
        }

        if (index >= 0)
            context.RemoveRange(index, context.Count - index); // 上下文回滚到该消息之前(连同本轮助手回复/工具消息一并移除)
        // 未找到且无压缩摘要:本轮失败/中断从未写回 → 上下文无需回滚,只清理显示

        // 显示回滚:移除该用户消息及其后全部条目(助手气泡、状态行、压缩标记等)
        var displayIndex = session.Messages.IndexOf(target);
        for (var i = session.Messages.Count - 1; i >= displayIndex; i--)
            session.Messages.RemoveAt(i);

        SaveSession(session); // 立即落盘:上下文与显示均已是回滚后状态
        return text.Length > 0 ? text : null;
    }

    private void OnLiveRunRequested()
    {
        // 直播间事件触发:回到 UI 线程,投递到绑定的会话(未绑定不触发;绑定会话已归档/删除则自动解绑)
        _dispatcher.BeginInvoke(() =>
        {
            var boundId = _host.BiliLive.BoundSessionId;
            if (string.IsNullOrEmpty(boundId))
                return;
            var bound = Sessions.FirstOrDefault(s => s.SessionId == boundId);
            if (bound is null)
            {
                _host.BiliLive.BindSession(null); // 绑定的会话已不在列表(归档/删除):自动解绑
                return;
            }
            _ = SendAsync(BiliLivePlugin.TriggerInput, bound);
        });
    }

    /// <summary>启动校验:$blive 持久化绑定指向的会话既不在列表也不在归档(已在他处删除或文件损坏)时解绑,
    /// 解绑经 BoundSessionChanged 自动清空持久化值。</summary>
    private void ValidateBiliBinding()
    {
        var bound = _host.BiliLive.BoundSessionId;
        if (bound is null)
            return;
        if (Sessions.Any(s => s.SessionId == bound) || ArchivedSessions().Any(s => s.Id == bound))
            return;
        _host.BiliLive.BindSession(null);
    }
}
