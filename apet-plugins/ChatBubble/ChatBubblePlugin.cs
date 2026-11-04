using APet.Framework.LLM;
using APet.Framework.Models;
using APet.Framework.Plugin;
using APet.Framework.Session;
using APet.Plugins.EmotionTags;
using AgentClass = APet.Framework.Agent.Agent;

namespace APet.Plugins.ChatBubble;

/// <summary>
/// 气泡锚定所需的宠物侧能力(消费者定义接口,由桌宠插件实现——气泡因此不反向依赖桌宠的具体类型)。
/// </summary>
public interface IPetAnchor
{
    /// <summary>宠物悬浮窗当前的屏幕矩形(拖动/缩放随动)。</summary>
    System.Drawing.Rectangle WindowBounds { get; }

    /// <summary>宠物窗口位置或尺寸变化(x, y 为新的屏幕坐标;气泡据此重算锚点)。</summary>
    event Action<int, int>? PositionChanged;

    /// <summary>情绪标签括号风格(与桌宠注入 system prompt、TTS 解析同源)。</summary>
    PetTagStyle TagStyle { get; }
}

/// <summary>
/// 对话气泡插件:一个无边框漫画气泡窗体,始终锚定在宠物窗口正上方。
/// 只流式显示助手的对话文本(思考/工具调用不显示),自动隐藏情绪 tag
/// (括号风格跟随桌宠插件的 TagStyle,解析规则与 TTS 一致),
/// 对话结束后停留片刻自动渐变隐藏;工具调用轮立即隐藏(工具执行期间不显示内容)。
/// 样式(字体/颜色/形状)全部经 [PluginSetting] 开放,由 BubbleStyle 统一夹取与解析,
/// 设置变更即时热应用到正在显示的气泡(重新排版+重新锚定,位置不漂移)。
/// </summary>
public sealed class ChatBubblePlugin : AgentPluginBase
{
    private const int HideDelayMs = 4000;     // 对话结束后气泡停留时间
    private const int TrackIntervalMs = 400;  // 可见期间轮询宠物窗口的间隔(拖动/缩放跟随)
    private const int PreviewHideDelayMs = 2000; // 偏移预览气泡的自动淡出延迟

    /// <summary>上偏移(px):正值朝宠物方向(下移);上方与侧置的顶端对齐都按它调整。</summary>
    [PluginSetting("topOffset", Description = "Top offset in px; positive moves toward the pet", Min = -300, Max = 300)]
    public int TopOffset { get; set; }

    /// <summary>左偏移(px):侧置于左侧时生效,正值朝宠物方向(右移)。</summary>
    [PluginSetting("leftOffset", Description = "Left offset in px (side placement); positive moves toward the pet", Min = -300, Max = 300)]
    public int LeftOffset { get; set; }

    /// <summary>右偏移(px):侧置于右侧时生效,正值朝宠物方向(左移)。</summary>
    [PluginSetting("rightOffset", Description = "Right offset in px (side placement); positive moves toward the pet", Min = -300, Max = 300)]
    public int RightOffset { get; set; }

    // ───────── 气泡样式(值合法性:颜色在 setter 归一化,数值在 BubbleStyle.From 统一夹取) ─────────

    /// <summary>气泡字体(系统字体名;不存在时渲染回退默认字体)。</summary>
    [PluginSetting("fontFamily", Description = "Bubble text font family name")]
    public string FontFamily { get; set; } = BubbleStyle.DefaultFontFamily;

    /// <summary>字号(磅,6–48)。</summary>
    [PluginSetting("fontSize", Description = "Font size in points", Min = 6, Max = 48)]
    public double FontSize { get; set; } = BubbleStyle.DefaultFontSize;

    /// <summary>字形(Regular/Bold/Italic/BoldItalic,枚举名大小写不敏感)。</summary>
    [PluginSetting("fontStyle", Description = "Regular / Bold / Italic / BoldItalic")]
    public BubbleFontStyle FontStyle { get; set; } = BubbleFontStyle.Regular;

    /// <summary>文字颜色(#RRGGBB;不支持半透明)。</summary>
    [PluginSetting("textColor", Description = "Text color as #RRGGBB")]
    public string TextColor { get => _textColor; set => _textColor = NormalizeColor(value, _textColor); }
    private string _textColor = BubbleStyle.DefaultTextColor;

    /// <summary>气泡底色(#RRGGBB)。</summary>
    [PluginSetting("fillColor", Description = "Bubble fill color as #RRGGBB")]
    public string FillColor { get => _fillColor; set => _fillColor = NormalizeColor(value, _fillColor); }
    private string _fillColor = BubbleStyle.DefaultFillColor;

    /// <summary>边框颜色(#RRGGBB)。</summary>
    [PluginSetting("borderColor", Description = "Bubble border color as #RRGGBB")]
    public string BorderColor { get => _borderColor; set => _borderColor = NormalizeColor(value, _borderColor); }
    private string _borderColor = BubbleStyle.DefaultBorderColor;

    /// <summary>边框粗细(px,0–10;0 = 无边框)。</summary>
    [PluginSetting("borderWidth", Description = "Border width in px; 0 hides the border", Min = 0, Max = 10)]
    public double BorderWidth { get; set; } = BubbleStyle.DefaultBorderWidth;

    /// <summary>圆角半径(px,0–40;0 = 直角;超出气泡尺寸时自动收缩,不会画出越界的圆弧)。</summary>
    [PluginSetting("cornerRadius", Description = "Corner radius in px; 0 = square", Min = 0, Max = 40)]
    public int CornerRadius { get; set; } = BubbleStyle.DefaultCornerRadius;

    /// <summary>尾巴长度(px,0–40;0 = 无尾巴的圆角卡片)。</summary>
    [PluginSetting("tailLength", Description = "Tail length in px; 0 hides the tail", Min = 0, Max = 40)]
    public int TailLength { get; set; } = BubbleStyle.DefaultTailLength;

    /// <summary>气泡体最大宽(px,160–800;超过则换行并出现滚动)。</summary>
    [PluginSetting("maxWidth", Description = "Bubble body max width in px before wrapping", Min = 160, Max = 800)]
    public int MaxWidth { get; set; } = BubbleStyle.DefaultMaxWidth;

    /// <summary>整体不透明度(%,30–100;淡入淡出以它为目标值)。</summary>
    [PluginSetting("opacityPercent", Description = "Whole-bubble opacity in percent", Min = 30, Max = 100)]
    public int OpacityPercent { get; set; } = BubbleStyle.DefaultOpacityPercent;

    /// <summary>颜色串归一化:合法则回写规范 #RRGGBB,非法保持现值(UI 只提交合法值,脏配置不生效)。</summary>
    private static string NormalizeColor(string value, string current) =>
        BubbleStyle.TryParseColor(value, out var color) ? BubbleStyle.ToHex(color) : current;

    /// <summary>由当前设置值构造气泡样式(夹取/解析统一在 BubbleStyle.From)。</summary>
    private BubbleStyle BuildStyle() => BubbleStyle.From(
        FontFamily, FontSize, FontStyle.ToString(),
        TextColor, FillColor, BorderColor,
        BorderWidth, CornerRadius, TailLength, MaxWidth, OpacityPercent);

    /// <summary>点击气泡请求打开聊天窗口(UI 层订阅,与点宠物同一入口:长回复进主窗看全文)。</summary>
    public event Action? OpenChatRequested;

    private readonly IPetAnchor _pet;
    private EmotionTagStream? _display; // 本轮流式剥离器:增量剥 tag + 扣留半个 tag,产出即气泡显示文本

    private BubbleForm? _form;
    private System.Windows.Forms.Timer? _hideTimer;
    private System.Windows.Forms.Timer? _trackTimer;
    private System.Drawing.Rectangle _lastPetBounds = System.Drawing.Rectangle.Empty;
    private bool _finishedPending; // 对话结束、等待自动隐藏(悬停气泡可挂起倒计时)
    private bool _bubbleHover;     // 鼠标正悬停在气泡上(WinForms 无 IsMouseOver,用 Enter/Leave 自维护)

    // 偏移预览:设置页调滑块时临时显示一个占位气泡,让用户看到大致落点(与正式气泡互不干扰)
    private BubbleForm? _previewForm;
    private System.Windows.Forms.Timer? _previewTimer;

    public ChatBubblePlugin(IPetAnchor pet)
    {
        _pet = pet;
    }

    /// <summary>是否仍在朗读语音(宿主注入 TTS 的队列/播放状态;null = 视为不在朗读)。
    /// 朗读期间气泡保持显示,全部朗读完毕才开始隐藏倒计时。</summary>
    public Func<bool>? IsSpeechActive { get; set; }

    private string? _agentName;

    /// <summary>宿主设置 Agent 显示名:气泡窗标题形如 "APet - 优香(对话气泡)",供 OBS 按窗口标题识别捕获。</summary>
    public void SetAgentName(string name)
    {
        _agentName = name;
        if (_form is not null)
            _form.Text = $"APet - {name}(对话气泡)";
    }

    public override string Name => "chat-bubble";

    // ───────── 生命周期 ─────────

    /// <summary>消息链钩子运行在 UI 线程(与桌宠窗口同线程),WinForms 定时器直接在此创建。</summary>
    public override void Initialize()
    {
        _hideTimer = new System.Windows.Forms.Timer { Interval = HideDelayMs };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer!.Stop();
            _form?.StartHide();
        };

        _trackTimer = new System.Windows.Forms.Timer { Interval = TrackIntervalMs };
        _trackTimer.Tick += (_, _) => TrackPet();

        _previewTimer = new System.Windows.Forms.Timer { Interval = PreviewHideDelayMs };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer!.Stop();
            _previewForm?.StartHide();
        };

        _pet.PositionChanged += OnPetMoved;
    }

    public override void Dispose()
    {
        _pet.PositionChanged -= OnPetMoved;
        if (_hideTimer is not null)
        {
            _hideTimer.Stop();
            _hideTimer.Dispose();
            _hideTimer = null;
        }
        if (_trackTimer is not null)
        {
            _trackTimer.Stop();
            _trackTimer.Dispose();
            _trackTimer = null;
        }
        if (_form is not null)
        {
            _form.Close();
            _form.Dispose();
            _form = null;
        }
        if (_previewTimer is not null)
        {
            _previewTimer.Stop();
            _previewTimer.Dispose();
            _previewTimer = null;
        }
        if (_previewForm is not null)
        {
            _previewForm.Close();
            _previewForm.Dispose();
            _previewForm = null;
        }
    }

    // ───────── 偏移预览(设置页调滑块时调用) ─────────

    /// <summary>
    /// 在宠物身边临时显示一个占位气泡,让用户直观看到当前偏移与样式下的效果。
    /// 每次调用刷新位置并重置自动淡出计时;宠物窗口未显示时不显示。与正式对话气泡互不影响。
    /// style 为 null 时使用插件当前样式;设置页会在保存前传控件值以便实时预览。
    /// </summary>
    public void ShowOffsetPreview(int topOffset, int leftOffset, int rightOffset, BubbleStyle? style = null)
    {
        var bounds = _pet.WindowBounds;
        if (bounds.IsEmpty)
            return; // 宠物窗口未显示(未加载/已禁用):无锚点可预览

        _previewForm ??= new BubbleForm();
        var resolved = style ?? BuildStyle();
        _previewForm.ApplyStyle(resolved, bounds);
        // 占位文本长度接近典型回复:气泡宽度影响居中/侧置落点,太短会误导位置判断
        _previewForm.Present("你好呀!我就在宠物旁边说话。", bounds, topOffset, leftOffset, rightOffset);

        _previewTimer?.Stop();
        _previewTimer?.Start();
    }

    // ───────── 消息链钩子:只关心助手对话文本 ─────────

    /// <summary>新一轮运行开始:清掉上一轮残留(异常中断时可能没走到 Finished),气泡立即收起。</summary>
    public override Task OnBeforeRunAsync(AgentClass agent, AgentSession session, List<ChatMessage> messages)
    {
        _display = null; // 立即收起不受悬停影响
        _finishedPending = false;
        _hideTimer?.Stop();
        _form?.StartHide();
        return Task.CompletedTask;
    }

    public override void OnStreamDelta(AgentClass agent, AgentSession session, StreamDeltaContext delta)
    {
        switch (delta.Kind)
        {
            case StreamDeltaKind.Content:
                // 增量剥离(每增量只处理新增量):产出即显示文本,完整 tag 剥除、半个 tag 扣住不闪现
                RefreshBubble((_display ??= new EmotionTagStream(_pet.TagStyle)).Append(delta.Text));
                _hideTimer?.Stop(); // 新内容到来:取消待定的自动隐藏
                break;

            case StreamDeltaKind.Finished when delta.SourceEvent is FinishedEvent { Reason: FinishReason.ToolCalls }:
                // 工具调用轮:过渡文本不保留,立即收起(工具执行期间气泡不显示);立即收起不受悬停影响
                _display = null;
                _finishedPending = false;
                _hideTimer?.Stop();
                _form?.StartHide();
                break;

            case StreamDeltaKind.Finished:
                // 对话结束:轮次状态清空(窗体保留最后文本继续展示),停留片刻后渐变隐藏;
                // 悬停气泡可挂起倒计时;TTS 朗读期间不启动倒计时(气泡保持到朗读结束,由宿主经 OnTtsIdle 唤起)
                _display = null;
                _finishedPending = true;
                if (!TtsStillSpeaking)
                    StartHideCountdown();
                break;
        }
    }

    /// <summary>TTS 是否仍在朗读(朗读期间气泡保持显示,不进入隐藏倒计时)。</summary>
    private bool TtsStillSpeaking => IsSpeechActive?.Invoke() == true;

    /// <summary>排定结束后的自动隐藏;鼠标正悬停在气泡上时不启动(保持显示,MouseLeave 时再排定)。</summary>
    private void StartHideCountdown()
    {
        if (_bubbleHover)
            return; // 悬停中:气泡保持显示,等 MouseLeave 恢复倒计时
        _hideTimer?.Start(); // WinForms Timer:已运行时 Start 即重置倒计时
    }

    /// <summary>
    /// TTS 全部朗读完毕(宿主经 Dispatcher 回 UI 线程调用):
    /// 气泡若在对话结束后一直等朗读结束,此时才启动隐藏倒计时。
    /// </summary>
    public void OnTtsIdle()
    {
        if (_finishedPending)
            StartHideCountdown();
    }

    // ───────── 气泡刷新 ─────────

    private void RefreshBubble(string text)
    {
        var petBounds = _pet.WindowBounds;
        if (petBounds.IsEmpty)
            return; // 宠物窗口未显示(未加载/已禁用):无锚点,不显示气泡

        if (text.Length == 0)
            return; // 本轮目前只有 tag 或空白:暂不显示

        var form = EnsureForm();
        form.Present(text, petBounds, TopOffset, LeftOffset, RightOffset);
        _lastPetBounds = petBounds;
        _trackTimer?.Start();
    }

    /// <summary>设置变更:偏移立即重新锚定;样式设置热应用到正在显示的对话气泡与预览气泡。</summary>
    protected override void OnSettingChanged(string name)
    {
        var bounds = _pet.WindowBounds;
        if (name is "topOffset" or "leftOffset" or "rightOffset")
        {
            var form = _form;
            if (form is null || !form.Visible || bounds.IsEmpty)
                return;
            form.Reposition(bounds, TopOffset, LeftOffset, RightOffset);
            return;
        }
        if (!IsStyleSetting(name))
            return;
        // 样式变更:ApplyStyle 内部按新字体/宽度重新排版并沿用最近偏移重新锚定(不错位)
        _form?.ApplyStyle(BuildStyle(), bounds);
        _previewForm?.ApplyStyle(BuildStyle(), bounds);
    }

    /// <summary>是否为样式类设置(其余为偏移与插件内部状态)。</summary>
    private static bool IsStyleSetting(string name) =>
        name is "fontFamily" or "fontSize" or "fontStyle" or "textColor" or "fillColor" or "borderColor"
            or "borderWidth" or "cornerRadius" or "tailLength" or "maxWidth" or "opacityPercent";

    private BubbleForm EnsureForm()
    {
        if (_form is not null)
            return _form;
        _form = new BubbleForm();
        _form.ApplyStyle(BuildStyle(), _pet.WindowBounds); // 配置可能在建窗前已推送,建窗即套用
        if (_agentName is not null)
            _form.Text = $"APet - {_agentName}(对话气泡)";
        // 点气泡 → 打开聊天窗口(与点宠物同一入口;长回复在气泡里滚动看,点击直接进主窗看全文)
        _form.Click += (_, _) => OpenChatRequested?.Invoke();
        // 悬停挂起自动隐藏:结束后鼠标还停在气泡上则不开始倒计时,离开时恢复
        _form.MouseEnter += (_, _) =>
        {
            _bubbleHover = true;
            if (_finishedPending)
                _hideTimer?.Stop();
        };
        _form.MouseLeave += (_, _) =>
        {
            _bubbleHover = false;
            if (_finishedPending && !TtsStillSpeaking)
                StartHideCountdown(); // TTS 朗读未结束:继续等待,由 OnTtsIdle 唤起
        };
        return _form;
    }

    // ───────── 跟随宠物窗口(拖动/缩放) ─────────

    /// <summary>气泡可见期间,宠物窗口位置/大小变化时重新锚定。</summary>
    private void TrackPet()
    {
        var form = _form;
        if (form is null || !form.Visible)
        {
            _trackTimer?.Stop();
            return;
        }

        var bounds = _pet.WindowBounds;
        if (bounds.IsEmpty || bounds == _lastPetBounds)
            return;
        _lastPetBounds = bounds;
        form.Reposition(bounds, TopOffset, LeftOffset, RightOffset); // 只挪位置,不动文本与淡出状态(结束时 _raw 已清空)
    }

    private void OnPetMoved(int x, int y) => TrackPet();
}
