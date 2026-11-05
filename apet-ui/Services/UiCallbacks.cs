using System.IO;
using System.Windows.Threading;
using APet.Framework.Agent;
using APet.Framework.Events;
using APet.Framework.LLM;
using APet.Framework.Logging;
using APet.Framework.Models;
using APet.Framework.Security;
using APet.Framework.Session;
using APet.Framework.Tools;
using APet.Plugins.EmotionTags;
using APet.Plugins.SubAgent;
using APet.UI.ViewModels;
using APet.UI.Web;

namespace APet.UI.Services;

// ───────── 流式回调 → 会话 VM ─────────

/// <summary>把 Agent 流式回调转发到会话 VM(经 Dispatcher 回 UI 线程)。</summary>
public sealed class UiCallbacks : AgentCallbacks
{
    private readonly Dispatcher _dispatcher;
    private readonly SessionViewModel _session;
    private ChatMessageViewModel _assistant; // 可替换:运行中压缩翻页后指向新气泡
    private readonly Func<string, string> _displayName;
    private readonly Dictionary<string, ToolCallViewModel> _toolRows = new();
    private readonly AgentSecurityPolicy? _policy;
    private readonly SessionStore? _store; // 工具结果图片(如截图)落盘到会话图片目录
    private readonly Func<AgentConfig?>? _configProvider; // 计费:按会话当前 供应商/模型 的计费因子累计费用

    // 情绪 tag 剥离(按正文段一个实例):新正文段开始时先放行上一段扣留的残片
    private EmotionTagStream? _tagStripping;
    private ContentSegmentViewModel? _strippedSegment;

    /// <summary>情绪 tag 剥离工厂(非 null = 本轮开启隐藏情绪 Tag):每个正文段新建一个剥离器。</summary>
    public Func<EmotionTagStream>? EmotionTagStripping { get; set; }

    /// <summary>运行中压缩翻页:接收当前助手气泡,返回承接后续内容的新气泡(返回 null = 不翻,如气泡尚无内容)。</summary>
    public Func<ChatMessageViewModel, ChatMessageViewModel?>? AssistantBubbleRoll { get; set; }

    /// <summary>把当前助手气泡定格,后续正文/思考/工具行改写入新气泡 —— 运行中压缩的分割线由此保持在旧内容之后。</summary>
    public void RollAssistantBubble()
    {
        if (AssistantBubbleRoll?.Invoke(_assistant) is { } created)
        {
            _assistant = created;
            _tagStripping = null; // 情绪剥离按正文段工作:新气泡从干净的段状态开始
            _strippedSegment = null;
        }
    }

    public UiCallbacks(Dispatcher dispatcher, SessionViewModel session, ChatMessageViewModel assistant,
        Func<string, string> displayNameResolver, AgentSecurityPolicy? securityPolicy = null, SessionStore? imageStore = null,
        Func<AgentConfig?>? configProvider = null)
    {
        _dispatcher = dispatcher;
        _session = session;
        _assistant = assistant;
        _displayName = displayNameResolver;
        _policy = securityPolicy;
        _store = imageStore;
        _configProvider = configProvider;
    }

    public override Task OnTurnStartedAsync(AgentSession session, ChatMessage assistantMessage, CancellationToken cancellationToken = default)
    {
        // 本轮请求开始:把该轮 context 助手消息 id 盖到当前气泡,本轮新建的正文/思考段自动带上
        // (气泡翻页后 _assistant 已指向新气泡,翻页前旧段保留各自 id,关联依然精确)
        _dispatcher.BeginInvoke(() =>
        {
            _assistant.TurnMessageId = assistantMessage.Id;
            // 气泡级回链:收集该轮全部 context 助手消息 id(落盘后前端可取回每条的原始用量/模型)
            if (!string.IsNullOrEmpty(assistantMessage.Id) && !_assistant.TurnMessageIds.Contains(assistantMessage.Id))
                _assistant.TurnMessageIds.Add(assistantMessage.Id);
        });
        return Task.CompletedTask;
    }

    public override Task OnThinkingDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default)
    {
        if (delta.Length == 0)
            return Task.CompletedTask; // 兜底:空增量不下发(个别兼容端点会发)
        _dispatcher.BeginInvoke(() => _assistant.AppendThinking(delta));
        return Task.CompletedTask;
    }

    public override Task OnContentDeltaAsync(AgentSession session, string delta, CancellationToken cancellationToken = default)
    {
        if (delta.Length == 0)
            return Task.CompletedTask; // 兜底:空正文增量不得触发 EndThinking,否则思考段被逐块打碎
        _dispatcher.BeginInvoke(() =>
        {
            _assistant.EndThinking();
            if (EmotionTagStripping is { } factory && delta.Length > 0)
                AppendContentStripped(delta, factory); // 隐藏情绪 Tag:剥离完整 tag、扣留半个 tag(与 TTS/气泡同规则)
            else
                _assistant.AppendContent(delta); // 正文按轮次分段进步骤流,与工具行保持时序
        });
        return Task.CompletedTask;
    }

    /// <summary>带剥离的正文追加:延续当前正文段(整段替换显示文本),或在新段落(工具调用/鉴权卡片之后)先放行上一段残片再新开剥离器。</summary>
    private void AppendContentStripped(string delta, Func<EmotionTagStream> factory)
    {
        if (_assistant.Steps.LastOrDefault() is ContentSegmentViewModel segment && ReferenceEquals(segment, _strippedSegment))
        {
            segment.Text = _tagStripping!.Append(delta); // 显示文本 = 原文剥 tag + 扣留半 tag;只增不改
            return;
        }
        FlushStrippedTail(); // 上一段已结束:扣留的残片按普通文本放行(新轮次不会再补全它)
        _strippedSegment = new ContentSegmentViewModel("") { MsgId = _assistant.TurnMessageId };
        _assistant.Steps.Add(_strippedSegment);
        _tagStripping = factory();
        _strippedSegment.Text = _tagStripping.Append(delta);
    }

    /// <summary>放行当前正文段扣留的尾部残片(按普通文本),并重置剥离状态。幂等。</summary>
    private void FlushStrippedTail()
    {
        if (_tagStripping is { } stripper && _strippedSegment is { } segment)
            segment.Text = stripper.Finish();
        _tagStripping = null;
        _strippedSegment = null;
    }

    public override Task OnCompletedAsync(AgentSession session, AgentResult result, CancellationToken cancellationToken = default)
    {
        // 运行正常结束:放行最后一个正文段可能扣留的尾部残片(排在全部增量之后执行,顺序安全)
        if (EmotionTagStripping is not null)
            _dispatcher.BeginInvoke(FlushStrippedTail);
        return Task.CompletedTask;
    }

    public override Task OnErrorAsync(AgentSession session, Exception exception, CancellationToken cancellationToken = default)
    {
        // 出错:先放行残片再追加状态行/错误文本,保证顺序(本 BeginInvoke 先于宿主 catch 的后续追加入队);
        // 这是运行期异常唯一的界面错误出口(红色状态行,仅展示层,不进 context),宿主侧不再重复追加
        if (EmotionTagStripping is not null)
            _dispatcher.BeginInvoke(FlushStrippedTail);
        PostError("✗ " + exception.Message);
        return Task.CompletedTask;
    }

    public override Task OnToolCallStartedAsync(AgentSession session, ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        // 工具调用行与鉴权卡片都渲染在助手消息内部,按发生顺序排列
        _dispatcher.BeginInvoke(() =>
        {
            _assistant.EndThinking();
            var row = ToolCallPresenter.Create(_displayName(toolCall.Name), toolCall.Name, toolCall.ArgumentsJson,
                _policy?.WorkingDirectory); // 会话工作目录:diff 行号定位需按它解析相对路径
            row.ToolCallId = toolCall.Id; // 与 context 消息的 tool_calls[].id 互链:落盘后工具行可回链发起/结果消息
            // sub_wait/sub_kill 运行期间即回填任务名(运行已存在),灰字不显示"运行 #N"
            if (SubRuns is { } runs)
                ToolCallPresenter.AttachSubRunTitle(row, id => runs.Find(id, _session.SessionId)?.Title);
            _assistant.AddToolCall(row);
            _toolRows[ToolRowKey(toolCall)] = row;
        });
        return Task.CompletedTask;
    }

    /// <summary>工具执行中的实时输出增量(运行终端工具的 stdout/stderr):追加到对应工具行的详情,
    /// 运行中展开即可边跑边看;快照与中断收口都按该行的最新详情出,输出不会丢。</summary>
    public override Task OnToolCallOutputAsync(AgentSession session, ToolCall toolCall, string stream, string delta, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(delta))
            return Task.CompletedTask;
        _dispatcher.BeginInvoke(() =>
        {
            if (_toolRows.TryGetValue(ToolRowKey(toolCall), out var row))
                row.AppendLiveOutput(delta);
        });
        return Task.CompletedTask;
    }

    public override Task OnToolCallFinishedAsync(AgentSession session, ToolCall toolCall, ToolResult result, CancellationToken cancellationToken = default)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_toolRows.TryGetValue(ToolRowKey(toolCall), out var row))
            {
                ToolCallPresenter.ApplyResult(row, result.Text);
                // 多模态工具(如截图):把结果图片外置为会话图片文件并挂到行上——展开可看原图,重启后按文件名读回不丢
                foreach (var dataUrl in result.ImageDataUrls)
                {
                    var name = _store?.StoreImageDataUrl(_session.SessionId, dataUrl);
                    if (name is not null)
                        row.AddImageFile(name);
                    var src = MainViewModel.DecodeDataUrlImage(dataUrl);
                    if (src is not null)
                        row.AddImage(src);
                }
            }
        });
        return Task.CompletedTask;
    }

    public override async Task<AuthorizationResult> OnToolCallAuthorizationAsync(
        AgentSession session,
        ToolCall toolCall,
        ToolEffect effect,
        AuthorizationResult decision,
        CancellationToken cancellationToken = default)
    {
        // 鉴权卡片插入助手消息的步骤流(与工具行同序),等待用户允许/拒绝(取消运行视为拒绝)
        var (detail, reason, command) = BuildAuthCard(toolCall, effect, decision);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionKey = ToolPermission.GetKey(toolCall.Name); // 文件修改类共用 "edit"
        var trustDirs = CollectTrustDirs(effect, permissionKey);
        AuthRequestViewModel card = null!;
        await _dispatcher.InvokeAsync(() =>
        {
            card = _assistant.AddAuthRequest(detail, reason, command, trustDirs, tcs);
            if (trustDirs.Count > 0)
                card.TrustRequested = dirs => ApplyTrust(dirs, permissionKey);
            AuthorizationPopupRequested?.Invoke(card);
        }).Task;
        using (cancellationToken.Register(() =>
        {
            tcs.TrySetResult(false);
            _dispatcher.BeginInvoke(() =>
            {
                card.Resolve(false);
                WebEventHub.CancelAuth(card.WebAuthId); // 中断的卡同步移出 Web 登记簿,否则永久泄漏
            });
        }))
        {
            var granted = await tcs.Task;
            return granted ? AuthorizationResult.Allow : AuthorizationResult.Deny("denied by user");
        }
    }

    /// <summary>受影响路径所在的目录列表(去重),供"信任此目录"。
    /// 只对真实存在的路径派生目录:解析器在未知命令上提取的记号(如未展开变量/URL 片段)
    /// 可能拼出不存在的"幻影目录",对其信任会写入无意义的白名单。
    /// 文件类工具(edit)例外:写入目标可以是尚未创建的新文件,路径不存在时取其直接
    /// 父目录(须已存在)作候选——新文件写入若连信任入口都没有,目录白名单形同虚设;
    /// 不向上追溯,2026-09 否掉的"上溯到最近已存在祖先"(过宽信任)行为仍不恢复。</summary>
    private static IReadOnlyList<string> CollectTrustDirs(ToolEffect effect, string? toolKey)
    {
        var dirs = new List<string>();
        var isFileTool = toolKey == "edit";
        foreach (var path in effect.Paths)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    var parent = isFileTool ? Path.GetDirectoryName(full) : null;
                    if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                        continue;
                    if (!dirs.Contains(parent, StringComparer.OrdinalIgnoreCase))
                        dirs.Add(parent);
                    continue;
                }
                var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(dir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // 非法路径跳过
            }
        }
        return dirs;
    }

    /// <summary>
    /// 信任目录:按当前工具的权限类别加入本轮运行策略(立即生效)与会话白名单(后续轮次持续有效)。
    /// 目录对 read 放行不代表对 edit/terminal 放行。
    /// </summary>
    private void ApplyTrust(IReadOnlyList<string> dirs, string toolKey)
    {
        foreach (var dir in dirs)
        {
            _policy?.AddWhitelistedDirectory(dir, toolKey);
            if (!_session.WhitelistedDirectories.Any(e =>
                    e.Directory.Equals(dir, StringComparison.OrdinalIgnoreCase) && e.Tool == toolKey))
                _session.WhitelistedDirectories.Add(new SessionWhitelistEntry(dir, toolKey));
        }
    }

    /// <summary>鉴权卡片加入步骤流后触发(UI 线程);宿主可在窗体不可见时弹独立窗口。</summary>
    public Action<AuthRequestViewModel>? AuthorizationPopupRequested { get; set; }

    /// <summary>每次 LLM 请求完成(含用量回报)时由宿主触发:立即把会话落盘,长任务中断/崩溃时已完成轮次不丢。</summary>
    public Action<SessionViewModel>? RequestSessionSave { get; set; }

    /// <summary>子代理运行管理器(宿主注入;中断兜底据它核对 sub_* 行的最终状态)。</summary>
    public SubAgentRunManager? SubRuns { get; set; }

    /// <summary>本轮计费锚定(本轮请求实际使用的供应商/模型;宿主在轮次起始按会话选择捕获)。
    /// 轮内用户切换模型只影响下一轮(连接挂起切换),但计费不能漂到切换后的"当前选择"——
    /// 必须按本轮真实请求所用模型计价。不设置 = 用会话当前选择(无运行锁的场景)。</summary>
    public (string Provider, string Model)? BillingAnchor { get; set; }

    /// <summary>中断兜底触发的工具行收口(宿主注入:向 Web 补发 tool_finished 帧,浏览器工具行同步收口)。</summary>
    public Action<ToolCallViewModel>? InterruptedToolRowFinalized { get; set; }

    /// <summary>
    /// 中断/出错兜底:主轮被停止时,还停在"运行中"的 sub_* 工具行(如 sub_wait 正阻塞等待)
    /// 不会再收到 OnToolCallFinishedAsync,行会永久挂在"运行中"。按子运行真实状态(主轮停止已联动取消子运行)
    /// 收口这些行,使落盘与 Web 快照得到终态。UI 线程调用(宿主 RunTurnAsync 的 finally 中)。
    /// </summary>
    public void FinalizeInterruptedToolRows()
    {
        foreach (var row in _toolRows.Values.ToList())
        {
            if (!row.IsRunning)
                continue;
            if (row.ToolName.StartsWith("sub_", StringComparison.Ordinal))
            {
                // sub_* 卡运行中:子运行已被主轮停止联动取消(或早已结束),按真实状态收口
                var text = "Subagent run was cancelled (main run stopped).";
                if (row.SubRunId is { } id)
                {
                    var run = SubRuns?.Find(id, _session.SessionId);
                    if (run is not null)
                        text = $"Subagent {run.Name} (run #{run.Id}, task: {run.Title}) "
                              + (run.Status == SubAgentStatus.Running
                                  ? "is still running; the wait was interrupted (main run stopped)."
                                  : "was cancelled (main run stopped).");
                }
                ToolCallPresenter.ApplyResult(row, text);
            }
            else
            {
                // 普通工具行卡在运行中(工具同步执行中被取消):直接标记中断(不走 JSON 解析)
                row.IsRunning = false;
                row.ResultSummary = "已中断";
            }
            InterruptedToolRowFinalized?.Invoke(row);
        }
    }

    public override Task OnUsageReportedAsync(AgentSession session, APet.Framework.LLM.LlmUsage usage, CancellationToken cancellationToken = default)
    {
        var anchor = BillingAnchor;
        _dispatcher.BeginInvoke(() =>
        {
            UsageAccounting.Apply(_session, _configProvider?.Invoke(), usage, anchor?.Provider, anchor?.Model);
            // 每次请求完成即落盘:强杀/崩溃/退出时磁盘已有全部已完成请求的用量,
            // 至多丢进行中的那一次(轮末 finally 的保存是第二道)
            try
            {
                RequestSessionSave?.Invoke(_session);
            }
            catch (Exception ex)
            {
                APet.Framework.Logging.Log.Global.Error($"逐请求落盘失败: {ex.Message}");
            }
        });
        return Task.CompletedTask;
    }


    private static string ToolRowKey(ToolCall toolCall) =>
        string.IsNullOrEmpty(toolCall.Id) ? $"{toolCall.Name}#{toolCall.ArgumentsJson}" : toolCall.Id;

    private string BuildAuthDetail(ToolCall toolCall, ToolEffect effect)
    {
        var sb = new System.Text.StringBuilder();
        var verb = effect.Access switch
        {
            FileAccessKind.Write => "请求写入",
            FileAccessKind.Read => "请求读取",
            FileAccessKind.None => "请求执行",
            _ => "请求执行",
        };
        sb.Append(_displayName(toolCall.Name)).Append(' ').Append(verb);
        if (!string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
            sb.Append('\n').Append(ToolCallPresenter.DescribeArguments(toolCall.Name, toolCall.ArgumentsJson));
        // 按"会触碰的目录"展示(文件取所在目录,目录本身原样):比逐文件更直观,
        // 且展示的目录即"信任此目录"会加入白名单的范围。
        // 全部路径都不存在时被跳过策略滤空 → 整行不显示,避免悬空的"范围: "
        var scopeDirs = ScopeDirectories(effect.Paths);
        if (scopeDirs.Count > 0)
            sb.Append("\n范围: ").Append(string.Join("  ", scopeDirs));
        return sb.ToString();
    }

    /// <summary>构建鉴权卡片三要素:纯文本详情(桌面弹窗沿用)、原因一行(档位/灾难判定的中文说明,
    /// 由 CheckTerminal 的拒绝理由给出)、终端命令原文(Web 卡片代码块完整展示)。
    /// 文件类工具无命令且拒绝理由是英文模板 → 均为 null,卡片沿用详情布局。</summary>
    private (string Detail, string? Reason, string? Command) BuildAuthCard(
        ToolCall toolCall, ToolEffect effect, AuthorizationResult decision)
    {
        var detail = BuildAuthDetail(toolCall, effect);
        string? reason = null;
        string? command = null;
        if (ToolPermission.GetKey(toolCall.Name) == "terminal")
        {
            if (!decision.Allowed && !string.IsNullOrWhiteSpace(decision.Reason)
                && decision.Reason != "denied by user")
                reason = decision.Reason;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(toolCall.ArgumentsJson);
                if (doc.RootElement.TryGetProperty("command", out var cmd))
                    command = cmd.GetString();
            }
            catch (System.Text.Json.JsonException)
            {
                // 参数非合法 JSON:命令展示缺省,详情仍可用
            }
        }
        return (detail, reason, command);
    }

    /// <summary>受影响路径 → 所在目录(去重):文件取其父目录,目录本身原样。</summary>
    private static List<string> ScopeDirectories(IReadOnlyList<string> paths)
    {
        var dirs = new List<string>();
        foreach (var path in paths)
        {
            string? dir;
            try
            {
                var full = Path.GetFullPath(path);
                // 与 CollectTrustDirs 同策略(用户决定,2026-09):不存在的路径直接跳过,
                // 不回退展示其父目录——否则不存在的目录(如 ...\桌面)会显示成用户主目录,
                // 范围展示与"信任此目录"候选口径保持一致
                if (!File.Exists(full) && !Directory.Exists(full))
                    continue;
                dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                dir = path;
            }
            if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                dirs.Add(dir);
        }
        return dirs;
    }

    private void PostStatus(string text) =>
        _dispatcher.BeginInvoke(() => _session.AddStatus(text));

    /// <summary>错误状态行(红色显示,随会话持久化;仅展示层,不进 context)。</summary>
    private void PostError(string text) =>
        _dispatcher.BeginInvoke(() => _session.AddErrorStatus(text));
}
