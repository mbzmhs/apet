using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using APet.Framework.LLM;
using APet.Framework.Session;

namespace APet.UI.ViewModels;

/// <summary>INotifyPropertyChanged 轻量基类。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, params string[] propertyNames)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        foreach (var name in propertyNames)
            Raise(name);
        return true;
    }
}

public enum MessageRole
{
    User,
    Assistant,
    Status,
    /// <summary>聊天记录分割线(压缩完成标记等)。</summary>
    Divider,
    /// <summary>压缩进行中的临时齿轮行(不落盘,结束即移除)。</summary>
    Spinner,
}

/// <summary>diff 详情的一行:行号 + 内容 + 种类(add/del/ctx,驱动红绿配色)。</summary>
public sealed record DiffLineViewModel(string LineNo, string Text, string Kind);

/// <summary>edit_file 的 diff 输入(结果阶段回填行号用:创建行时的预定位存在 UI 线程竞态,读到的是改后内容)。</summary>
public sealed record EditDiffInfo(string Path, string OldString, string NewString, string? Workdir);

/// <summary>
/// 助手消息内的一次工具调用行:头部为可读名称 + 摘要,点击可展开详情
/// (编辑/补丁类显示红绿 diff 行 + 行号,终端类显示命令与输出),不展示原始 JSON。
/// </summary>
public sealed class ToolCallViewModel : ObservableObject
{
    private string _summary = "";
    private string _detailBody = "";
    private string _resultSummary = "";
    private bool _isRunning = true;
    private IReadOnlyList<DiffLineViewModel> _diffLines = Array.Empty<DiffLineViewModel>();
    private readonly ObservableCollection<ImageSource> _images = new();
    private readonly List<string> _imageFiles = new();

    public ToolCallViewModel(string displayName, string toolName)
    {
        DisplayName = displayName;
        ToolName = toolName;
    }

    /// <summary>可读名称(来自工具 Definition.DisplayName)。</summary>
    public string DisplayName { get; }

    /// <summary>协议名(用于结果解析)。</summary>
    public string ToolName { get; }

    /// <summary>发起调用的 tool_call_id(与 context 消息的 tool_calls[].id / tool_call_id 互链;落盘,重启后还原)。</summary>
    public string? ToolCallId { get; set; }

    /// <summary>关联的子代理运行 id(sub_* 工具;非空时行可点击打开子代理模态框)。</summary>
    public int? SubRunId { get; set; }

    /// <summary>子代理任务名称(sub_run 的 title 参数;非空时"运行 #N"入口改显示它)。</summary>
    public string SubRunTitle { get; set; } = "";

    /// <summary>一行摘要(目标文件/命令等)。</summary>
    public string Summary
    {
        get => _summary;
        set => Set(ref _summary, value, nameof(Summary));
    }

    private string _pathLink = "";

    /// <summary>文件名链接文案(编辑/写入类工具;空 = 不显示)。点击用默认应用打开目标文件。</summary>
    public string PathLink
    {
        get => _pathLink;
        set => Set(ref _pathLink, value, nameof(PathLink));
    }

    /// <summary>链接打开的目标路径(原始参数路径,点击时按会话工作目录再解析)。</summary>
    public string? PathOpenTarget { get; set; }

    /// <summary>解析打开目标所用的会话工作目录(与工具执行时相对路径基准一致)。</summary>
    public string? PathWorkdir { get; set; }

    /// <summary>展开详情内容(纯文本兜底;编辑/补丁类另有 DiffLines 彩色行)。</summary>
    public string DetailBody
    {
        get => _detailBody;
        set => Set(ref _detailBody, value, nameof(DetailBody), nameof(HasDetail), nameof(HasTextDetail));
    }

    /// <summary>彩色 diff 行(编辑/补丁类工具)。</summary>
    public IReadOnlyList<DiffLineViewModel> DiffLines
    {
        get => _diffLines;
        private set => Set(ref _diffLines, value, nameof(DiffLines), nameof(HasDiff), nameof(HasDetail));
    }

    /// <summary>是否有彩色 diff 行可展示。</summary>
    public bool HasDiff => _diffLines.Count > 0;

    /// <summary>填充 diff 行(一次赋值,不逐行变更)。</summary>
    public void SetDiff(IReadOnlyList<DiffLineViewModel> lines) => DiffLines = lines;

    /// <summary>edit_file 的 diff 输入(结果阶段回填行号;不落盘,仅运行期使用)。</summary>
    public EditDiffInfo? EditDiff { get; set; }

    /// <summary>完成后的结果摘要(如 已写入 / 退出码 1)。</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        set => Set(ref _resultSummary, value, nameof(ResultSummary));
    }

    private string _statAdd = "";

    /// <summary>行数统计:新增行数(如 "+6",绿色显示;空 = 不显示)。编辑/补丁类工具填写。</summary>
    public string StatAdd
    {
        get => _statAdd;
        set => Set(ref _statAdd, value, nameof(StatAdd));
    }

    private string _statDel = "";

    /// <summary>行数统计:删除行数(如 "-3",红色显示;空 = 不显示)。编辑/补丁类工具填写。</summary>
    public string StatDel
    {
        get => _statDel;
        set => Set(ref _statDel, value, nameof(StatDel));
    }

    /// <summary>是否执行中(执行中显示齿轮,完成后显示对勾)。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value, nameof(IsRunning));
    }

    private bool _isError;

    /// <summary>结果是否失败(工具报错/命令非零退出/超时):结果摘要以红色显示。</summary>
    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value, nameof(IsError));
    }

    /// <summary>工具结果附带的图片(如截图),展开行内显示,点击可看原图。</summary>
    public ObservableCollection<ImageSource> Images => _images;

    /// <summary>结果图片的本地文件名(与 Images 一一对应,持久化用;还原时回填,保证重启再存不丢图)。</summary>
    public IReadOnlyList<string> ImageFiles => _imageFiles;

    /// <summary>是否有结果图片可展示。</summary>
    public bool HasImages => _images.Count > 0;

    /// <summary>记录一张结果图片的本地文件名(与 AddImage 配对,供落盘/还原)。</summary>
    public void AddImageFile(string fileName) => _imageFiles.Add(fileName);

    /// <summary>是否还有纯文本详情(截图类只有图片、无文本,展开时据此隐藏空文本框)。</summary>
    public bool HasTextDetail => !string.IsNullOrWhiteSpace(_detailBody);

    /// <summary>追加一张结果图片(如截图),并刷新可展开/有图标记。</summary>
    public void AddImage(ImageSource image)
    {
        _images.Add(image);
        Raise(nameof(HasImages));
        Raise(nameof(HasDetail));
    }

    /// <summary>运行中是否已收到过实时输出增量(首个增量前补一个换行:详情首行是命令原文)。</summary>
    private bool _liveOutputAppended;

    /// <summary>追加一段运行中实时输出(运行终端工具的 stdout/stderr 增量):展开即可边跑边看,
    /// 中断收口该行时已到的输出也不会丢。</summary>
    public void AppendLiveOutput(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;
        DetailBody = _liveOutputAppended || string.IsNullOrEmpty(DetailBody)
            ? DetailBody + delta
            : DetailBody + "\n" + delta;
        _liveOutputAppended = true;
    }

    /// <summary>是否有可展开的详情(文本 / 彩色 diff / 结果图片任一)。</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailBody) || _diffLines.Count > 0 || _images.Count > 0;
}

/// <summary>工具调用鉴权确认卡片:展示影响详情,等待用户允许/拒绝。</summary>
public sealed class AuthRequestViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs;
    private bool _decided;
    private bool _granted;

    public AuthRequestViewModel(string detail, string? reason, string? command, TaskCompletionSource<bool> tcs)
    {
        Detail = detail;
        Reason = reason;
        Command = command;
        _tcs = tcs;
    }

    /// <summary>鉴权描述(工具可读名/参数摘要/影响范围)。</summary>
    public string Detail { get; }

    /// <summary>需要确认的原因一行(档位/灾难判定的中文说明;文件类工具为 null,沿用详情布局)。</summary>
    public string? Reason { get; }

    /// <summary>终端命令原文(Web 卡片以代码块完整展示;文件类工具为 null)。</summary>
    public string? Command { get; }

    /// <summary>可信任的候选目录(受影响路径所在的文件夹)。</summary>
    public IReadOnlyList<string> TrustDirs { get; init; } = Array.Empty<string>();

    public bool HasTrustDirs => TrustDirs.Count > 0;

    /// <summary>点击"信任此目录"时触发(宿主加入会话白名单),随后自动按允许处理。</summary>
    public Action<IReadOnlyList<string>>? TrustRequested { get; set; }

    /// <summary>Web 形态的授权解析标识(WebEventHub 登记;快照随卡片下发,浏览器据此 REST 解析)。</summary>
    public string? WebAuthId { get; set; }

    /// <summary>是否已有结论。</summary>
    public bool Decided
    {
        get => _decided;
        private set => Set(ref _decided, value, nameof(Decided), nameof(ResultText));
    }

    /// <summary>结论是否为允许。</summary>
    public bool Granted => _granted;

    /// <summary>结论文本(拒绝时展示为"已拒绝本次操作";允许时整卡隐藏,不展示)。</summary>
    public string ResultText => !_decided ? "" : _granted ? "已允许本次执行" : "已拒绝本次操作";

    /// <summary>信任受影响目录:加入会话白名单并放行本次调用。</summary>
    public void Trust()
    {
        TrustRequested?.Invoke(TrustDirs);
        Resolve(true);
    }

    /// <summary>用户给出结论(允许/拒绝);重复调用忽略。</summary>
    public void Resolve(bool granted)
    {
        if (_decided)
            return;
        _granted = granted;
        Decided = true;
        _tcs.TrySetResult(granted);
    }
}

/// <summary>助手消息中的一段正文(多轮工具调用时正文按轮次分段,保持时序)。</summary>
public sealed class ContentSegmentViewModel : ObservableObject
{
    private string _text;

    public ContentSegmentViewModel(string text) => _text = text;

    /// <summary>本段正文所属的 context 助手消息 id(创建时由所属气泡的当前轮次消息 id 盖入;落盘)。</summary>
    public string? MsgId { get; set; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value, nameof(Text));
    }
}

/// <summary>会话级白名单条目:目录 + 工具权限类别(ToolPermission 键)。</summary>
public sealed record SessionWhitelistEntry(string Directory, string Tool);

/// <summary>
/// 一段思考(步骤流内的折叠块):每次模型调用产出一段,
/// 工具调用/正文开始后该段结束并计时,下一轮思考另起新段,保持时序。
/// </summary>
    public sealed class ThinkingSegmentViewModel : ObservableObject
    {
        private readonly Stopwatch _watch = new();
        private string _text = "";
        private bool _isActive;
        private double _seconds = -1;

        /// <summary>本段思考所属的 context 助手消息 id(创建时由所属气泡的当前轮次消息 id 盖入;落盘)。</summary>
        public string? MsgId { get; set; }

    /// <summary>思考全文(展开后可查看与复制)。</summary>
    public string Text
    {
        get => _text;
        private set => Set(ref _text, value, nameof(Text), nameof(Preview));
    }

    /// <summary>是否正在输出思考(折叠头部显示“思考中”)。</summary>
    public bool IsActive
    {
        get => _isActive;
        private set => Set(ref _isActive, value, nameof(IsActive), nameof(Header));
    }

    /// <summary>折叠头部文案:思考中 / 思考 · N.N 秒 / 思考。</summary>
    public string Header => IsActive ? "思考中"
        : _seconds >= 0 ? $"思考 · {_seconds:N1} 秒"
        : "思考";

    /// <summary>折叠头部预览:最新一行思考内容(一行放不下时截断,靠左显示)。</summary>
    public string Preview
    {
        get
        {
            var trimmed = _text.TrimEnd('\r', '\n');
            if (trimmed.Length == 0)
                return "";
            int newline = trimmed.LastIndexOfAny(['\r', '\n']);
            var line = (newline < 0 ? trimmed : trimmed[(newline + 1)..]).Trim();
            return line.Length > 200 ? line[^200..] : line;
        }
    }

    /// <summary>本段思考耗时(未结束时为 null;持久化还原时用)。</summary>
    public double? Seconds => _seconds >= 0 ? _seconds : null;

    /// <summary>流式追加思考增量。</summary>
    public void Append(string delta)
    {
        if (!IsActive)
        {
            _watch.Restart();
            IsActive = true;
        }
        Text += delta;
    }

    /// <summary>本段思考结束(正文/工具调用开始或请求终止),记录耗时。</summary>
    public void End()
    {
        if (!IsActive)
            return;
        _watch.Stop();
        _seconds = _watch.Elapsed.TotalSeconds;
        IsActive = false;
    }

    /// <summary>从持久化数据还原一个已结束的思考段(不触发计时)。</summary>
    public void Restore(string text, double seconds)
    {
        _text = text;
        _seconds = seconds;
        IsActive = false;
    }
}

/// <summary>用户消息的一个文本文件附件:气泡内显示为文件卡片(图标 + 文件名),点击打开文本查看器;内容在发送时内联进模型正文。</summary>
public sealed class TextFileAttachmentViewModel
{
    /// <summary>文件名(展示用)。</summary>
    public string Name { get; init; } = "";

    /// <summary>大小描述(tooltip,如 1.2 MB)。</summary>
    public string SizeText { get; init; } = "";

    /// <summary>文件全文(文本查看器展示)。</summary>
    public string Content { get; init; } = "";

    /// <summary>悬停提示:文件名 + 大小。</summary>
    public string Tooltip => SizeText.Length > 0 ? $"{Name}({SizeText})" : Name;
}

/// <summary>聊天区一条消息(气泡)。</summary>
public sealed class ChatMessageViewModel : ObservableObject
{
    private string _text = "";
    private bool _isBusy;

    private bool _ephemeral;

    public ChatMessageViewModel(MessageRole role, string text = "", DateTime? time = null)
    {
        Role = role;
        _text = text;
        Time = time ?? DateTime.Now;
    }

    public MessageRole Role { get; }

    private bool _isError;

    /// <summary>错误状态(状态行红色显示;工具调用行为结果摘要红色)。</summary>
    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value, nameof(IsError));
    }

    /// <summary>宿主分配的用户消息标识(GUID):与本轮写入 context 的用户消息同一 Id,供 /undo 精确匹配;随 Display 落盘。</summary>
    public string? ClientId { get; set; }

    /// <summary>助手气泡的回合关联:触发本轮的用户消息 id(发送时由宿主盖入;仅助手气泡有值),随 Display 落盘。</summary>
    public string? RunClientId { get; set; }

    /// <summary>当前轮次(最近一次 LLM 请求)的 context 助手消息 id:框架 OnTurnStarted 回调盖入,
    /// 新建正文/思考段时抄给步骤,落盘后步骤可精确回链 context 消息。非当前轮步骤保留各自 id。</summary>
    public string? TurnMessageId { get; set; }

    /// <summary>本气泡对应的 context 消息 id 集合:助手气泡 = 该轮全部助手消息 id(按请求顺序,
    /// OnTurnStarted 逐请求收集,含中间工具调用消息——每条自带用量/模型);用户气泡 = 自身 ClientId
    /// (即 context 用户消息 id,发送时两者同值)。随 Display 落盘、随 Web 快照下发:
    /// 前端凭它取回原始消息(用量/模型/思考/工具参数)显示详情。</summary>
    public List<string> TurnMessageIds { get; } = new();

    /// <summary>气泡对应的 context 消息 id 集合(落盘/快照统一取此)。</summary>
    public List<string> ContextMessageIds() => Role == MessageRole.User
        ? (ClientId is { Length: > 0 } c ? [c] : [])
        : [.. TurnMessageIds];

    /// <summary>临时条目(不落盘):保存 Display 时跳过,重启后不还原。</summary>
    public bool Ephemeral
    {
        get => _ephemeral;
        set => Set(ref _ephemeral, value);
    }

    /// <summary>消息创建时间。</summary>
    public DateTime Time { get; }

    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>助手消息运行中(压缩翻页/插入索引等逻辑判据;运行结束或被移除时置 false)。
    /// 可见的"工作中"指示已移到输入框上下文圆盘左侧的旋转齿轮,不再渲染在气泡里。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value, nameof(IsBusy));
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value, nameof(Text));
    }

    /// <summary>本轮运行的步骤(正文分段、工具调用行与鉴权卡片,按发生顺序交错排列)。</summary>
    public ObservableCollection<object> Steps { get; } = new();

    /// <summary>用户消息携带的图片缩略图(气泡内横向展示;助手/状态消息为空)。</summary>
    public ObservableCollection<ImageSource> Images { get; } = new();

    /// <summary>与 Images 对应的本地图片文件名(该会话独立图片目录 images/{sessionId}/ 下;随会话落盘,重启后按名读回)。</summary>
    public List<string> ImageFiles { get; } = new();

    /// <summary>是否携带图片(驱动气泡内缩略图行的可见性)。</summary>
    public bool HasImages => Images.Count > 0;

    /// <summary>用户消息携带的文本文件附件(气泡内文件卡片行;助手/状态消息为空)。</summary>
    public ObservableCollection<TextFileAttachmentViewModel> TextFiles { get; } = new();

    /// <summary>是否携带文本文件(驱动气泡内文件卡片行的可见性)。</summary>
    public bool HasTextFiles => TextFiles.Count > 0;

    /// <summary>记录一次工具调用(摘要/详情由 ToolCallPresenter 填充)。</summary>
    public ToolCallViewModel AddToolCall(ToolCallViewModel row)
    {
        Steps.Add(row);
        return row;
    }

    /// <summary>加入一张鉴权确认卡片,返回卡片供更新。</summary>
    public AuthRequestViewModel AddAuthRequest(string detail, string? reason, string? command,
        IReadOnlyList<string> trustDirs, TaskCompletionSource<bool> tcs)
    {
        var card = new AuthRequestViewModel(detail, reason, command, tcs) { TrustDirs = trustDirs };
        Steps.Add(card);
        return card;
    }

    /// <summary>追加正文增量:延续最近一段正文,或新开一段(工具调用后产生新正文时)。</summary>
    public void AppendContent(string delta)
    {
        if (delta.Length == 0)
            return;
        if (Steps.LastOrDefault() is ContentSegmentViewModel segment)
        {
            segment.Text += delta;
            return;
        }
        Steps.Add(new ContentSegmentViewModel(delta) { MsgId = TurnMessageId });
    }

    // ───────── 思考(按轮次分段,渲染在步骤流内) ─────────

    /// <summary>全部思考文本拼接(供“复制思考内容”菜单)。</summary>
    public string ThinkingText => string.Join("\n", Steps.OfType<ThinkingSegmentViewModel>().Select(t => t.Text));

    /// <summary>流式追加思考:延续正在输出的思考段,否则新开一段(工具调用后的新思考另起一块)。</summary>
    public void AppendThinking(string delta)
    {
        if (Steps.LastOrDefault() is ThinkingSegmentViewModel active && active.IsActive)
        {
            active.Append(delta);
            Raise(nameof(ThinkingText));
            return;
        }
        var segment = new ThinkingSegmentViewModel { MsgId = TurnMessageId };
        Steps.Add(segment);
        segment.Append(delta);
        Raise(nameof(ThinkingText));
    }

    /// <summary>结束当前思考段(正文/工具调用开始或请求终止)。</summary>
    public void EndThinking() => (Steps.LastOrDefault() as ThinkingSegmentViewModel)?.End();
}

/// <summary>一个对话会话(承载框架 AgentSession 与其聊天渲染)。</summary>
public sealed class SessionViewModel : ObservableObject
{
    private string _title = "新对话";
    private bool _isStreaming;

    public SessionViewModel(AgentSession session)
    {
        Session = session;
        SessionId = session.Id; // 框架会话 GUID:即持久化文件名
        CreatedAt = DateTime.Now;
        Messages.CollectionChanged += OnMessagesCollectionChanged; // 消息增删 → 刷新最后一条时间
        RefreshLastMessageTime();
    }

    /// <summary>持久化还原:沿用原会话 Id 与创建时间(文件名保持不变,更新覆盖原文件)。</summary>
    public SessionViewModel(AgentSession session, string sessionId, DateTime createdAt)
    {
        Session = session;
        SessionId = sessionId;
        CreatedAt = createdAt;
        Messages.CollectionChanged += OnMessagesCollectionChanged; // 消息增删 → 刷新最后一条时间
        RefreshLastMessageTime();
    }

    public AgentSession Session { get; }

    /// <summary>会话稳定标识(GUID;持久化文件名,重命名/归档不影响)。</summary>
    public string SessionId { get; }

    /// <summary>会话创建时间(持久化保留)。</summary>
    public DateTime CreatedAt { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value, nameof(Title));
    }

    /// <summary>该会话是否正在运行 Agent(期间禁用输入)。发送瞬间即置位:FIFO 排队等待也算工作状态,
    /// 期间不可再向本会话发送,发送按钮显示中断。</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value, nameof(IsStreaming));
    }

    /// <summary>桌宠 FIFO 排队中的轮次占位气泡(排队期间显示"思考中");出队开跑或取消时清空。</summary>
    public ChatMessageViewModel? PendingAssistant { get; set; }

    /// <summary>排队中的轮次被用户取消(停止按钮):出队时按此标记丢弃。</summary>
    public bool PendingTurnCancelled { get; set; }

    /// <summary>会话级 LLM 选择:供应商名(每个会话独立,可用不同模型同时工作;工具栏只编辑当前选中会话)。</summary>
    public string ChatProvider { get; set; } = "";

    /// <summary>会话级 LLM 选择:模型名(限定在该供应商的模型清单内)。</summary>
    public string ChatModel { get; set; } = "";

    /// <summary>会话级思考等级(归一化规范值;改动经重建连接生效)。</summary>
    public string ThinkingEffort { get; set; } = "";

    // ───────── 模型切换分割线 ─────────

    /// <summary>上一轮实际使用的供应商(模型按轮固定:轮内切换挂起、下轮生效;与当前选择不一致时
    /// 新一轮在用户消息上方插入「模型由 X 切换为 Y」分割线;随会话落盘;空 = 尚未跑过轮)。</summary>
    public string LastRoundProvider { get; set; } = "";

    /// <summary>上一轮实际使用的模型名(与 LastRoundProvider 成对)。</summary>
    public string LastRoundModel { get; set; } = "";

    /// <summary>运行中压缩完成后的气泡翻页回调(仅运行轮次设置):把助手气泡翻到分割线之后的新气泡,后续内容
    /// 不再灌进旧气泡;非运行期的 /compact 无回调、不翻页。</summary>
    public Action? CompressionBubbleRoll { get; set; }

    private bool _isCompressing;

    /// <summary>上下文压缩进行中(列表黄点);压缩占着运行锁,期间 IsStreaming 也为 true,圆点按此优先显示黄色。</summary>
    public bool IsCompressing
    {
        get => _isCompressing;
        set => Set(ref _isCompressing, value, nameof(IsCompressing));
    }

    private bool _hasError;

    /// <summary>最近一次运行出错(列表红点);选中该会话(已看到)或后续发送成功后清除。</summary>
    public bool HasError
    {
        get => _hasError;
        set => Set(ref _hasError, value, nameof(HasError));
    }

    private bool _unread;

    /// <summary>有新消息未读:不在本会话时收到回复(列表蓝点);选中该会话时清除。</summary>
    public bool Unread
    {
        get => _unread;
        set => Set(ref _unread, value, nameof(Unread));
    }

    private DateTime _lastMessageTime = DateTime.MinValue;
    private string _lastMessageTimeText = "";

    /// <summary>最后一条消息(含状态/分割线等通知,不含临时齿轮行)的时间。</summary>
    public DateTime LastMessageTime => _lastMessageTime;

    /// <summary>最后一条消息的相对时间文本(列表右侧灰色显示,如"刚刚"/"3分前")。</summary>
    public string LastMessageTimeText
    {
        get => _lastMessageTimeText;
        private set => Set(ref _lastMessageTimeText, value, nameof(LastMessageTimeText));
    }

    private int? _lastPromptTokens;

    /// <summary>最近一次请求的实际上下文占用(usage.prompt_tokens;null=尚未获得)。</summary>
    public int? LastPromptTokens
    {
        get => _lastPromptTokens;
        set => Set(ref _lastPromptTokens, value, nameof(LastPromptTokens));
    }

    // ───────── 会话累计 LLM 用量(状态栏持久化展示;随会话落盘,重开继续累加) ─────────

    private int _usagePromptTotal;

    /// <summary>会话累计 prompt tokens(每请求 usage 相加)。</summary>
    public int UsagePromptTotal
    {
        get => _usagePromptTotal;
        set => Set(ref _usagePromptTotal, value, nameof(UsagePromptTotal));
    }

    private int _usageCachedTotal;

    /// <summary>会话累计 Prompt 缓存命中 tokens。</summary>
    public int UsageCachedTotal
    {
        get => _usageCachedTotal;
        set => Set(ref _usageCachedTotal, value, nameof(UsageCachedTotal));
    }

    private int _usageCompletionTotal;

    /// <summary>会话累计输出 tokens。</summary>
    public int UsageCompletionTotal
    {
        get => _usageCompletionTotal;
        set => Set(ref _usageCompletionTotal, value, nameof(UsageCompletionTotal));
    }

    private double _usageLastTps;

    /// <summary>最近一次请求的输出速度(tokens/s)。</summary>
    public double UsageLastTps
    {
        get => _usageLastTps;
        set => Set(ref _usageLastTps, value, nameof(UsageLastTps));
    }

    private double _usageCost;

    /// <summary>会话累计费用(元;按模型计费因子逐请求累计)。</summary>
    public double UsageCost
    {
        get => _usageCost;
        set => Set(ref _usageCost, value, nameof(UsageCost));
    }

    private readonly object _usageGate = new();

    /// <summary>原子累计一次请求的用量:全部多字段在锁内一批更新,写者(主循环/子代理/压缩)可能来自
    /// 不同线程,锁保证读-改-写不竞态、各字段口径一致。
    /// 输出 = completion:官方文档口径 completion_tokens 已包含 reasoning_tokens,不得再叠加
    /// (reasoning 仅作逐消息留档/单独展示,见 MessageUsage)。
    /// updateLiveIndicators:占用条(最近 prompt 大小)与 TPS 是"本会话主对话"的实时指示——
    /// 子代理的请求(独立上下文、不同模型)只进累计、不改这两个指示。</summary>
    public void AddUsage(LlmUsage usage, double cost, bool updateLiveIndicators = true)
    {
        lock (_usageGate)
        {
            if (updateLiveIndicators)
                LastPromptTokens = usage.PromptTokens;
            UsagePromptTotal += usage.PromptTokens;
            UsageCachedTotal += usage.CachedTokens;
            UsageCompletionTotal += usage.CompletionTokens;
            if (updateLiveIndicators && usage.Tps > 0)
                UsageLastTps = usage.Tps;
            if (cost > 0)
                UsageCost += cost;
        }
    }

    private string _workingDirectory = "";   // 会话级工作目录,默认空

    /// <summary>会话级工作目录(工具安全鉴权用),默认为空。</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => Set(ref _workingDirectory, value, nameof(WorkingDirectory));
    }

    private string _workingDirectoryAccess = "ReadWrite";  // 会话级工作目录权限

    /// <summary>会话级工作目录权限:ReadOnly / ReadWrite / FullAccess。</summary>
    public string WorkingDirectoryAccess
    {
        get => _workingDirectoryAccess;
        set => Set(ref _workingDirectoryAccess, value, nameof(WorkingDirectoryAccess));
    }

    private string _scene = "";   // 注入的情景描述,默认空

    /// <summary>注入的情景描述($scene 指令):随会话持久化,拼进 system prompt;多次注入以最后一次为准。</summary>
    public string Scene
    {
        get => _scene;
        set => Set(ref _scene, value, nameof(Scene));
    }

    /// <summary>会话已产生对话后工作目录锁定,不可再修改。</summary>
    public bool IsWorkDirLocked
    {
        get => _workDirLocked;
        private set => Set(ref _workDirLocked, value, nameof(IsWorkDirLocked));
    }

    private bool _workDirLocked;

    /// <summary>首次发送后锁定工作目录。</summary>
    public void LockWorkDirectory() => IsWorkDirLocked = true;

    /// <summary>本会话内用户通过"信任此目录"加入白名单的目录(仅当前会话有效,按工具权限类别限定)。</summary>
    public List<SessionWhitelistEntry> WhitelistedDirectories { get; } = new();

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    /// <summary>本会话输入框草稿文本(每个会话独立,切换会话互不影响;不持久化)。</summary>
    public string DraftText { get; set; } = "";

    /// <summary>本会话待发送附件草稿(每个会话独立,切换会话互不影响;不持久化)。</summary>
    public ObservableCollection<AttachmentViewModel> DraftAttachments { get; } = new();

    /// <summary>用户上一条消息发出后是否发生过上下文压缩(压缩不可撤销):为 true 时 /undo 需等再发送一条新消息后才能撤销;随会话落盘,重启不丢。</summary>
    public bool CompactedSinceLastSend { get; set; }

    /// <summary>已归档会话的只读预览实例(Web 点开归档项时从磁盘懒恢复):
    /// 不出现在会话列表,禁发新消息/命令/压缩/撤销,仅供回看;不落盘(归档标记在 StoredSession.Archived)。</summary>
    public bool IsArchived { get; set; }

    public ChatMessageViewModel AddUser(string text, DateTime? time = null) => Add(MessageRole.User, text, time);

    /// <summary>添加携带图片附件的用户消息(气泡缩略图行 + 本地文件名供持久化)。</summary>
    public ChatMessageViewModel AddUserWithImages(string text, IEnumerable<(ImageSource Image, string FileName)> images, DateTime? time = null)
    {
        var message = Add(MessageRole.User, text, time);
        foreach (var (image, fileName) in images)
        {
            message.Images.Add(image);
            message.ImageFiles.Add(fileName);
        }
        return message;
    }

    /// <summary>创建一个流式输出中的助手气泡,返回实例供回调持续追加。</summary>
    public ChatMessageViewModel AddAssistant(DateTime? time = null)
    {
        var message = Add(MessageRole.Assistant, "", time);
        message.IsBusy = true;
        return message;
    }

    public ChatMessageViewModel AddStatus(string text, DateTime? time = null, int? index = null) => Add(MessageRole.Status, text, time, index);

    /// <summary>添加一条错误状态行(红色显示,仅展示层,不进 LLM 上下文);随会话持久化。</summary>
    public void AddErrorStatus(string text, DateTime? time = null)
    {
        var message = Add(MessageRole.Status, text, time);
        message.IsError = true;
    }

    /// <summary>追加压缩完成标记行(两侧横线+中间"会话已压缩";还原时传入原时间,避免默认成"刚刚")。</summary>
    public ChatMessageViewModel AddDivider(int? index = null, DateTime? time = null) =>
        AddDivider("会话已压缩", index, time);

    /// <summary>追加自定义文字分割标记行(两侧横线+中间文字,如"压缩失败"/"已中断")。</summary>
    public ChatMessageViewModel AddDivider(string text, int? index = null, DateTime? time = null) =>
        Add(MessageRole.Divider, text, time, index);

    /// <summary>追加压缩进行中的标记行(两侧横线+中间"上下文压缩中";临时条目不落盘,成功时由完成标记原位替换,中断/失败时移除,不留痕迹)。</summary>
    public ChatMessageViewModel AddSpinner(int? index = null)
    {
        var message = Add(MessageRole.Spinner, "上下文压缩中", null, index);
        message.Ephemeral = true;
        return message;
    }

    private ChatMessageViewModel Add(MessageRole role, string text, DateTime? time = null, int? index = null)
    {
        var message = new ChatMessageViewModel(role, text, time);
        if (index is int i && i >= 0 && i < Messages.Count)
            Messages.Insert(i, message);
        else
            Messages.Add(message);
        return message;
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshLastMessageTime();

    /// <summary>重算最后一条消息时间及其相对文本(消息增删或定时刷新时调用)。
    /// 只取用户/助手/状态等真实条目;分割线是结构标记且其时间在还原时易失真,不参与计时。</summary>
    public void RefreshLastMessageTime()
    {
        var latest = DateTime.MinValue;
        foreach (var m in Messages)
            if (!m.Ephemeral && m.Role != MessageRole.Divider && m.Time > latest)
                latest = m.Time;
        _lastMessageTime = latest;
        LastMessageTimeText = latest == DateTime.MinValue ? "" : RelativeTimeText(latest);
    }

    /// <summary>把时间映射为易读相对文本:刚刚 / N分前 / N小时前 / N天前 / N周前 / N个月前 / N年前。</summary>
    public static string RelativeTimeText(DateTime time)
    {
        var diff = DateTime.Now - time;
        if (diff.TotalSeconds < 60) return "刚刚"; // 含时钟漂移导致的负值兜底
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}分前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}小时前";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}天前";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}周前";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)}个月前";
        return $"{(int)(diff.TotalDays / 365)}年前";
    }
}
