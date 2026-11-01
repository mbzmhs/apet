using System.Text.Json;
using APet.Framework.Logging;
using APet.Framework.Plugin;
using APet.Framework.Session;
using APet.Plugins.ChatBubble;
using APet.Plugins.EmotionTags;
using APet.Renderer;
using APet.Renderer.Live2D;

namespace APet.Plugins.DesktopPet;

/// <summary>
/// 桌宠显示插件:统一管理渲染引擎(Spine / Live2D / VRM / 基础,基础由宿主经工厂注入),
/// 在独立透明悬浮窗显示宠物模型。能力:
/// - 待机动作(多个时按定时轮换);周期性眨眼(可配置动作与速率,仅待机时触发、播完恢复原待机);
/// - 情感映射(tag 名 → 动作/表情):由 TTS 插件解析到情感 tag 时经 TriggerEmotion 驱动,
///   与发声严格同步,本插件不自行扫描流式文本;Live2D 的表情映射走参数级叠层,不占动作轨;
/// - 单击/双击/右键可配置响应:无 / 随机动作 / 回待机 / 打开聊天窗口;
/// - 对外暴露 PlayMotion / TriggerEmotion 等统一接口,其他插件可借此操控任意引擎的宠物。
/// </summary>
public sealed class DesktopPetPlugin : AgentPluginBase, IPetAnchor
{
    private const int DoubleClickMs = 400;

    private IPetRenderer? _renderer;
    private bool _initialized;   // Initialize 是否已执行:启用前收到的设置只记忆,不建窗(见 OnSettingChanged)
    private System.Windows.Forms.Timer? _clickTimer;
    private System.Windows.Forms.Timer? _idleTimer;
    private System.Windows.Forms.Timer? _blinkTimer;
    private bool _blinkActive;
    private string? _resumeAfterBlink;
    private bool _pendingSingleClick;
    private long _lastLeftClickMs;

    private List<string> _idleNames = [];
    private Dictionary<string, List<string>> _emotionMap = new(StringComparer.OrdinalIgnoreCase);
    // TTS 朗读期间保持情绪动作(Spine/Live2D 的非循环动作播完定格、不回待机),
    // 朗读结束由 OnTtsIdle 统一切回随机待机;基础引擎的保持由渲染器自身实现,不经此标志
    private bool _holdMotionUntilTtsEnd;

    // 悬浮窗位置(屏幕像素):由宿主持久化到配置、启动时经 SetWindowPosition 恢复;未设置时默认 (450,100)
    private int _posX = 450;
    private int _posY = 100;

    public override string Name => "desktop-pet";

    // ───────── 设置(引擎 / 模型 / 动作映射 / 交互) ─────────

    [PluginSetting("engine", Description = "Render engine: spine / live2d / vrm / basic (image folder, host-provided)",
        Options = new[] { "spine", "live2d", "vrm", "basic" })]
    public string Engine { get; set; } = "spine";

    [PluginSetting("modelPath", Description = "Model path (Spine: .skel/.json skeleton; Live2D: .model3.json; basic: image folder)", MaxLength = 512)]
    public string ModelPath { get; set; } = "";

    /// <summary>基础引擎:切换动作图片时的淡入淡出过渡(默认关闭)。</summary>
    [PluginSetting("fadeTransition", Description = "Basic engine: cross-fade when switching motions (default off)")]
    public bool FadeTransition { get; set; }

    /// <summary>基础引擎:淡入淡出过渡时长(毫秒)。</summary>
    [PluginSetting("fadeDurationMs", Description = "Basic engine: cross-fade duration in ms", Min = 50, Max = 5000)]
    public int FadeDurationMs { get; set; } = 250;

    /// <summary>VRM 引擎:渲染风格(toon / pbr / mtoon)。</summary>
    [PluginSetting("shadingStyle", Description = "VRM engine: render style (toon / pbr / mtoon)",
        Options = new[] { "mtoon", "toon", "pbr" })]
    public string ShadingStyle { get; set; } = "mtoon";

    [PluginSetting("atlasPath", Description = "Spine atlas override (empty = auto-find beside the skeleton)", MaxLength = 512)]
    public string AtlasPath { get; set; } = "";

    /// <summary>显示缩放因子(屏幕像素 / 骨架单位,1 = 模型原始尺寸)。</summary>
    [PluginSetting("scale", Description = "Display scale factor (screen pixels per skeleton unit; 1 = original size)", Min = 0.05, Max = 8)]
    public float Scale { get; set; } = 1f;

    /// <summary>待机动作列表,JSON 字符串数组,如 ["Idle","Idle2"];多个时按圈轮换。</summary>
    [PluginSetting("idleMotions", Description = "Idle motions as JSON array, e.g. [\"Idle\",\"Idle2\"]", MaxLength = 8192)]
    public string IdleMotions { get; set; } = "[]";

    /// <summary>待机动作触发间隔(毫秒):每隔约该时长轮换一个待机动作。</summary>
    [PluginSetting("idleIntervalMs", Description = "Base interval between idle motion triggers (ms)", Min = 500, Max = 3600000)]
    public int IdleIntervalMs { get; set; } = 10000;

    /// <summary>待机触发的随机前后差(毫秒):实际间隔 = 间隔 ± 该值。</summary>
    [PluginSetting("idleJitterMs", Description = "Random +/- jitter applied to the idle interval (ms)", Min = 0, Max = 3600000)]
    public int IdleJitterMs { get; set; } = 3000;

    /// <summary>眨眼动作名(空 = 不眨眼);待机时周期性播放一次(不循环)。</summary>
    [PluginSetting("blinkMotion", Description = "Blink motion name (empty = disabled); plays once during idle, not looped", MaxLength = 512)]
    public string BlinkMotion { get; set; } = "";

    /// <summary>眨眼触发间隔(毫秒):每隔约该时长眨一次眼。</summary>
    [PluginSetting("blinkIntervalMs", Description = "Base interval between blinks (ms)", Min = 500, Max = 3600000)]
    public int BlinkIntervalMs { get; set; } = 4000;

    /// <summary>眨眼触发的随机前后差(毫秒):实际间隔 = 间隔 ± 该值。</summary>
    [PluginSetting("blinkJitterMs", Description = "Random +/- jitter applied to the blink interval (ms)", Min = 0, Max = 3600000)]
    public int BlinkJitterMs { get; set; } = 2000;

    /// <summary>情感映射,JSON 对象 tag → 动作数组,如 {"happy":["Jump"]}。</summary>
    [PluginSetting("emotionMap", Description = "Emotion tag to motions mapping as JSON object", MaxLength = 16384)]
    public string EmotionMap { get; set; } = "{}";

    /// <summary>VRM 引擎:导入的 VRMA 动作文件绝对路径列表(JSON 字符串数组)。情绪映射里的动作名即文件基名。</summary>
    [PluginSetting("vrmaPaths", Description = "VRM engine: imported VRMA animation file paths as JSON array", MaxLength = 16384)]
    public string VrmaPaths { get; set; } = "[]";

    /// <summary>兜底部件不透明度,JSON 对象 部件Id → 0..1(Live2D):模型加载完成后应用为默认形态;
    /// 表情淡出恢复到该基线而非 setup,防止多形态角色在表情未触发时露出默认形态。</summary>
    [PluginSetting("partOpacities", Description = "Fallback part opacities as JSON object {\"PartId\":0..1}; applied after model load (Live2D only)", MaxLength = 16384)]
    public string PartOpacities { get; set; } = "{}";

    /// <summary>Live2D 内建自动眨眼开关(CubismAutoEyeBlink,默认开);Spine 引擎忽略(用 blinkMotion 动作)。</summary>
    [PluginSetting("autoBlink", Description = "Live2D builtin auto eye-blink toggle (ignored by Spine)")]
    public bool AutoBlink { get; set; } = true;

    /// <summary>Live2D 呼吸动画开关(周期性身体起伏,默认开);Spine 引擎忽略。</summary>
    [PluginSetting("breath", Description = "Live2D breath animation toggle (ignored by Spine)")]
    public bool Breath { get; set; } = true;

    /// <summary>情感 tag 包裹风格:决定注入系统提示词的格式,并由宿主同步给 TTS 用于解析。</summary>
    [PluginSetting("tagStyle", Description = "Emotion tag wrapper style; injected into the system prompt and synced to TTS for parsing")]
    public PetTagStyle TagStyle { get; set; } = PetTagStyle.SquareBrackets;

    [PluginSetting("clickAction", Description = "Action on single click")]
    public PetClickAction ClickAction { get; set; } = PetClickAction.OpenChat;

    [PluginSetting("doubleClickAction", Description = "Action on double click")]
    public PetClickAction DoubleClickAction { get; set; } = PetClickAction.RandomMotion;

    [PluginSetting("rightClickAction", Description = "Action on right click")]
    public PetClickAction RightClickAction { get; set; } = PetClickAction.None;

    // ───────── 对宿主/其他插件开放的统一接口 ─────────

    /// <summary>点击动作要求打开聊天窗口(UI 层订阅,激活主窗体)。</summary>
    public event Action? OpenChatRequested;

    /// <summary>用户拖动宠物窗口后触发,参数为新的左上角屏幕坐标(宿主持久化到配置)。</summary>
    public event Action<int, int>? PositionChanged;

    /// <summary>当前渲染引擎是否已成功加载模型。</summary>
    public bool IsModelLoaded => _renderer?.IsLoaded == true;

    /// <summary>宠物悬浮窗的屏幕包围盒(左上角 + 尺寸);未显示时返回 Empty。供对话气泡等锚定在宠物正上方。</summary>
    public System.Drawing.Rectangle WindowBounds => _renderer?.GetWindowBounds() ?? System.Drawing.Rectangle.Empty;

    /// <summary>最近一次模型加载失败的错误信息。</summary>
    public string? LoadError => _renderer?.LoadError;

    /// <summary>
    /// Live2D 引擎的 Stage(捏表情等精细编辑用):仅 engine=live2d 且模型已加载时非空。
    /// </summary>
    public Live2DStage? Live2DStage => Engine.Trim().ToLowerInvariant() == "live2d"
        ? (_renderer as Live2DPetRenderer)?.Stage
        : null;

    /// <summary>
    /// 暂停待机轮换与眨眼的自动排程(捏表情编辑期间调用):动作每帧会覆盖其驱动的参数,
    /// 不暂停的话滑杆调的值会立刻被动作改回去。恢复请调用 <see cref="ResumeAutomation"/>。
    /// </summary>
    public void SuspendAutomation()
    {
        _idleTimer?.Stop();
        _blinkTimer?.Stop();
    }

    /// <summary>恢复自动排程并回到待机(编辑窗口关闭时调用)。</summary>
    public void ResumeAutomation()
    {
        PlayIdle();
        ScheduleBlink();
    }

    /// <summary>当前模型可用的动作名列表(设置页的动作选择数据源)。</summary>
    public string[] GetAvailableMotions() => _renderer?.GetMotionNames() ?? [];

    /// <summary>当前模型可用动作的详细信息(设置页展示用,含语义提示与时长)。</summary>
    public PetMotionInfo[] GetMotionInfos() => _renderer?.GetMotionInfos() ?? [];

    /// <summary>播放一个动作(loop=false 时播完自动回待机)。供其他插件/宿主程序化驱动。</summary>
    public bool PlayMotion(string motionName)
    {
        if (_renderer?.IsLoaded != true)
            return false;
        // 打断待机轮换与待播眨眼:播完该一次性动作后由 OnMotionCompleted 回待机并重新排程
        _idleTimer?.Stop();
        _blinkActive = false;
        _renderer.Play(motionName, loop: false);
        return true;
    }

    /// <summary>按情感映射播放动作(tag 为拆解后的名称,如 "happy";未映射则忽略,映射多个时随机选一个)。由 TTS 解析到 tag 时经宿主调用。</summary>
    public bool TriggerEmotion(string tag)
    {
        if (_renderer?.IsLoaded != true || !_emotionMap.TryGetValue(tag.Trim(), out var motions) || motions.Count == 0)
            return false;
        // 朗读/情绪期间不再随机轮换待机;动作播完回待机时由 PlayIdle 重新计时
        _idleTimer?.Stop();
        _holdMotionUntilTtsEnd = true; // 情绪动作保持到朗读结束,由 OnTtsIdle 统一切回待机
        _blinkActive = false; // 情感动作优先,取代待播眨眼
        _renderer.TriggerEmotion(motions);
        return true;
    }

    /// <summary>随机播放一个非待机动作(无可用动作则回退到全部动作)。</summary>
    public bool TriggerRandomMotion()
    {
        if (_renderer?.IsLoaded != true)
            return false;
        var all = _renderer.GetMotionNames();
        var pool = all.Where(n => !_idleNames.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pool.Length == 0)
            pool = all;
        if (pool.Length == 0)
            return false;
        _blinkActive = false; // 随机动作优先,取代待播眨眼
        _idleTimer?.Stop();   // 播放期间不轮换待机
        _renderer.Play(pool[Random.Shared.Next(pool.Length)], loop: false);
        return true;
    }

    /// <summary>
    /// TTS 朗读结束(宿主经 Dispatcher 回 UI 线程调用):切回一个随机待机动作(轮换计时随之重置)。
    /// 基础引擎的静态情绪图、Spine/Live2D 保持中的情绪动作都由这里统一切回;
    /// 当前已在待机时不动,避免无谓重播。
    /// </summary>
    public void OnTtsIdle()
    {
        var isBasic = string.Equals(Engine, "basic", StringComparison.OrdinalIgnoreCase);
        var wasHolding = _holdMotionUntilTtsEnd;
        _holdMotionUntilTtsEnd = false;
        if (!isBasic && !wasHolding)
            return;

        var current = _renderer?.CurrentMotion;
        if (current is not null && _idleNames.Contains(current, StringComparer.OrdinalIgnoreCase))
            return;
        PlayIdle();
    }

    /// <summary>设置/恢复悬浮窗位置(屏幕像素)。宿主在启用前调用以落位;已加载则立即移动,否则记住待加载后应用。</summary>
    public void SetWindowPosition(int x, int y)
    {
        _posX = x;
        _posY = y;
        if (_renderer?.IsLoaded == true)
            _renderer.SetPosition(x, y);
    }

    private string? _agentName;

    /// <summary>
    /// 宿主设置 Agent 显示名:渲染悬浮窗标题形如 "APet - 优香(Spine)",供 OBS 按窗口标题识别捕获。
    /// 引擎重建(切换引擎/换模型)时自动按新引擎名刷新。
    /// </summary>
    public void SetAgentName(string name)
    {
        _agentName = name;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        if (_agentName is null || _renderer is null)
            return;
        _renderer.SetWindowTitle($"APet - {_agentName}({_renderer.EngineDisplayName})");
    }

    // ───────── 生命周期 ─────────

    public override void Initialize()
    {
        ReloadMappings();
        // 单击延迟判定:400ms 内的第二次左键视为双击(取消已排队的单击)
        _clickTimer = new System.Windows.Forms.Timer { Interval = DoubleClickMs };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (!_pendingSingleClick)
                return;
            _pendingSingleClick = false;
            ExecuteClickAction(ClickAction);
        };
        // 待机定时触发:每隔 idleIntervalMs ± idleJitterMs 轮换一个待机动作(多个待机时)
        _idleTimer = new System.Windows.Forms.Timer();
        _idleTimer.Tick += (_, _) => OnIdleTick();
        // 眨眼定时触发:每隔 blinkIntervalMs ± blinkJitterMs 在待机时眨一次眼
        _blinkTimer = new System.Windows.Forms.Timer();
        _blinkTimer.Tick += (_, _) => OnBlinkTick();
        ReloadRenderer();
        _initialized = true;
        Log.Global.Info($"[DesktopPet] initialized (engine={Engine}, model='{ModelPath}')");
    }

    public override void Dispose()
    {
        _clickTimer?.Stop();
        _clickTimer?.Dispose();
        _clickTimer = null;
        _idleTimer?.Stop();
        _idleTimer?.Dispose();
        _idleTimer = null;
        _blinkTimer?.Stop();
        _blinkTimer?.Dispose();
        _blinkTimer = null;
        _renderer?.Dispose();
        _renderer = null;
        _initialized = false;
        Log.Global.Info("[DesktopPet] disposed");
    }

    protected override void OnSettingChanged(string name)
    {
        // 宿主构造顺序是"先推送设置、后 SetEnabled→Initialize":启用前收到的设置只记忆数据类项,
        // 渲染器统一由 Initialize 创建。若此处提前建窗,多 Agent 启动时会快速创建/随即销毁 GL 上下文,
        // 拖垮其余桌宠窗口(表现为开第二个后全部消失)。
        if (!_initialized)
        {
            if (name is "idleMotions" or "emotionMap" or "vrmaPaths")
                ReloadMappings();
            return;
        }
        if (!Enabled)
            return;   // 已初始化但当前被禁用:不操作渲染器
        switch (name)
        {
            case "engine":
            case "modelPath":
            case "atlasPath":
                ReloadRenderer();
                break;
            case "idleMotions":
                ReloadMappings();
                PlayIdle();
                break;
            case "idleIntervalMs":
            case "idleJitterMs":
                ScheduleIdle();
                break;
            case "blinkMotion":
            case "blinkIntervalMs":
            case "blinkJitterMs":
                ScheduleBlink();
                break;
            case "scale":
                _renderer?.SetScale(Scale);
                break;
            case "fadeTransition":
            case "fadeDurationMs":
                _renderer?.SetTransition(FadeTransition, FadeDurationMs);
                break;
            case "shadingStyle":
                _renderer?.SetShading(ShadingStyle.Trim().ToLowerInvariant() switch
                {
                    "toon" => 0,
                    "pbr" => 1,
                    _ => 2,
                });
                break;
            case "emotionMap":
                ReloadMappings();
                break;
            case "vrmaPaths":
                ReloadMappings();
                break;
            case "partOpacities":
                _renderer?.SetPartOpacities(ParsePartMap(PartOpacities)); // 已加载时立即应用新基线
                break;
            case "autoBlink":
                _renderer?.SetAutoEyeBlink(AutoBlink);
                break;
            case "breath":
                _renderer?.SetBreathEnabled(Breath);
                break;
        }
    }

    /// <summary>
    /// 把已配置的情感 tag(情感映射的 key)注入系统提示词,让 LLM 知道可用哪些表情标签、
    /// 按 TagStyle 包裹输出(如 [happy])——TTS 据此切音色、桌宠据此切表情。无映射时不注入。
    /// key 排序保证提示词稳定(缓存友好)。
    /// </summary>
    public override string? GetSystemPromptSegment(AgentSession session)
    {
        if (_emotionMap.Count == 0)
            return null;
        var (open, close) = TagWrapper();
        var tags = string.Join(", ", _emotionMap.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => open + k + close));
        return "You can express your emotional state by including an emotion tag within your reply, e.g. " + open + "happy" + close + ". "
             + "Available emotion tags: " + tags + ". "
             + "Use them naturally and sparingly to match the tone of what you are saying; do not explain or list the tags.";
    }

    /// <summary>按 TagStyle 返回 tag 的包裹符(宿主会把同一风格同步给 TTS,保证解析一致)。</summary>
    private (string Open, string Close) TagWrapper() => EmotionTagCodec.Wrapper(TagStyle);

    // ───────── 渲染引擎管理 ─────────

    private void ReloadRenderer()
    {
        _renderer?.Dispose();
        _renderer = CreateRenderer(Engine);
        _renderer.ModelClicked += OnModelClicked;
        _renderer.MotionCompleted += OnMotionCompleted;
        _renderer.PositionMoved += (x, y) => PositionChanged?.Invoke(x, y);
        UpdateWindowTitle(); // 窗口标题带引擎名,引擎重建后按新引擎刷新

        // 先按持久化坐标落位(在 Load 建窗之前),避免窗口从默认坐标闪现再跳
        _renderer.SetPosition(_posX, _posY);
        // 兜底形态:渲染器记住部件基线,Load 完成后应用(表情淡出恢复到该基线)
        _renderer.SetPartOpacities(ParsePartMap(PartOpacities));
        // 基础引擎的淡入淡出过渡(其余引擎空操作)
        _renderer.SetTransition(FadeTransition, FadeDurationMs);
        // VRM 引擎的渲染风格(其余引擎空操作)
        _renderer.SetShading(ShadingStyle.Trim().ToLowerInvariant() switch
        {
            "toon" => 0,
            "pbr" => 1,
            _ => 2,
        });
        // Live2D 内建眨眼/呼吸开关:重建渲染器后按用户设定恢复(Load 会重置 AutoEyeBlink)
        _renderer.SetAutoEyeBlink(AutoBlink);
        _renderer.SetBreathEnabled(Breath);
        // VRMA 动作路径:这里是新建渲染器实例(旧实例随 Dispose 丢弃),必须主动补推,
        // 否则新实例动画表为空;且 vrmaPaths 值未变化不会触发 OnSettingChanged 来补救。
        // Load 内会按已存路径重建动画表。
        _renderer.SetAnimations(ParseStringArray(VrmaPaths));

        var path = ModelPath?.Trim();
        if (path is { Length: > 0 })
        {
            var error = _renderer.Load(path, string.IsNullOrWhiteSpace(AtlasPath) ? null : AtlasPath.Trim());
            if (error is not null)
                Log.Global.Error("[DesktopPet] load failed: " + error);
        }
        // Load 内按默认因子(1)适配窗口,这里覆盖为用户配置的显示缩放
        _renderer.SetScale(Scale);
        PlayIdle();
        ScheduleBlink();
    }

    /// <summary>按引擎标识创建渲染器(四引擎齐备,直接构造;引擎实现都在 apet-renderer)。</summary>
    private IPetRenderer CreateRenderer(string engine) => engine.Trim().ToLowerInvariant() switch
    {
        "live2d" => new Live2DPetRenderer(),
        "vrm" => new VrmPetRenderer(),
        "basic" => new BasicPetRenderer(),
        _ => new SpinePetRenderer(),
    };

    private void ReloadMappings()
    {
        _idleNames = ParseStringArray(IdleMotions);
        _emotionMap = ParseEmotionMap(EmotionMap);
        _renderer?.SetAnimations(ParseStringArray(VrmaPaths));
    }

    private static List<string> ParseStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, List<string>> ParseEmotionMap(string json)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            return map?.Where(kv => kv.Key.Trim().Length > 0)
                       .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                   ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, float> ParsePartMap(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, float>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    // ───────── 待机 / 动作状态机 ─────────

    /// <summary>Live2D 的待机一次性播放、播完回休息形态(不循环);其余引擎待机持续循环。</summary>
    private bool OneShotIdle => string.Equals(_renderer?.Engine, "live2d", StringComparison.OrdinalIgnoreCase);

    private void PlayIdle()
    {
        if (_renderer?.IsLoaded != true || _idleNames.Count == 0)
            return;
        PlayIdleName(_idleNames[Random.Shared.Next(_idleNames.Count)]);
    }

    /// <summary>播放指定待机动作。Spine 循环播放,轮换计时从开播起算;Live2D 一次性播放,
    /// 间隔从播完(OnMotionCompleted)起算,此处不排程。</summary>
    private void PlayIdleName(string name)
    {
        _renderer?.Play(name, loop: !OneShotIdle);
        if (!OneShotIdle)
            ScheduleIdle();
    }

    /// <summary>待机定时触发:轮换到另一个待机动作(尽量不重复当前)。</summary>
    private void OnIdleTick()
    {
        if (_renderer?.IsLoaded != true || (_idleNames.Count <= 1 && !OneShotIdle))
            return; // Spine 单一待机持续循环无需轮换;Live2D 播完即回原状,单一待机也要定时重触发
        string next;
        if (_idleNames.Count == 1)
        {
            next = _idleNames[0]; // Live2D 单一待机:直取(避免 do/while 恒等死循环)
        }
        else
        {
            var current = _renderer.CurrentMotion;
            do // 尽量不重复当前
            {
                next = _idleNames[Random.Shared.Next(_idleNames.Count)];
            }
            while (string.Equals(next, current, StringComparison.OrdinalIgnoreCase));
        }
        PlayIdleName(next);
    }

    /// <summary>排程下一次待机触发(间隔 ± 随机差);Spine 单一待机持续循环不排程,Live2D 需排程重触发。</summary>
    private void ScheduleIdle()
    {
        if (_idleTimer is null || (_idleNames.Count <= 1 && !OneShotIdle))
            return;
        _idleTimer.Interval = NextIdleDelayMs();
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    /// <summary>下一次待机触发的延迟:间隔 + 随机前后差,下限 500ms。</summary>
    private int NextIdleDelayMs()
    {
        var jitter = IdleJitterMs > 0 ? Random.Shared.Next(-IdleJitterMs, IdleJitterMs + 1) : 0;
        return Math.Max(500, IdleIntervalMs + jitter);
    }

    /// <summary>排程下一次眨眼;未配置眨眼动作或模型未加载则停止。</summary>
    private void ScheduleBlink()
    {
        if (_blinkTimer is null)
            return;
        var blink = BlinkMotion?.Trim();
        if (string.IsNullOrEmpty(blink) || _renderer?.IsLoaded != true)
        {
            _blinkTimer.Stop();
            return;
        }
        _blinkTimer.Interval = NextBlinkDelayMs();
        _blinkTimer.Stop();
        _blinkTimer.Start();
    }

    /// <summary>下一次眨眼的延迟:间隔 + 随机前后差,下限 500ms。</summary>
    private int NextBlinkDelayMs()
    {
        var jitter = BlinkJitterMs > 0 ? Random.Shared.Next(-BlinkJitterMs, BlinkJitterMs + 1) : 0;
        return Math.Max(500, BlinkIntervalMs + jitter);
    }

    /// <summary>眨眼定时触发:仅在待机时眨一次眼(不循环),播完由 OnMotionCompleted 恢复原待机。</summary>
    private void OnBlinkTick()
    {
        var blink = BlinkMotion?.Trim();
        if (_renderer?.IsLoaded == true && !string.IsNullOrEmpty(blink))
        {
            var current = _renderer.CurrentMotion;
            // 情感/随机动作播放中不眨眼,避免打断
            if (current is not null && _idleNames.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                _resumeAfterBlink = current;
                _blinkActive = true;
                _renderer.Play(blink, loop: false);
            }
        }
        ScheduleBlink(); // 重新排程(含新的随机差)
    }

    /// <summary>动作播完:眨眼恢复被它打断的待机;一次性动作回待机并重新排程待机轮换。</summary>
    private void OnMotionCompleted(string motionName)
    {
        // TTS 朗读期间保持情绪动作(非循环动作播完定格在末姿势、不回待机),朗读结束由 OnTtsIdle 统一切回;
        // 待机动作的圈完成回调不受影响(基础引擎的非循环动作不走本回调)
        if (_holdMotionUntilTtsEnd && !_idleNames.Contains(motionName, StringComparer.OrdinalIgnoreCase))
            return;

        if (_blinkActive)
        {
            // 眨眼播完:恢复之前那个待机(不重置待机轮换节奏)
            _blinkActive = false;
            var resume = _resumeAfterBlink;
            _resumeAfterBlink = null;
            if (resume is not null && _renderer?.IsLoaded == true && _idleNames.Contains(resume, StringComparer.OrdinalIgnoreCase))
                _renderer.Play(resume, loop: !OneShotIdle);
            else
                PlayIdle();
            return;
        }
        // Live2D:还有别的动作在播(如打断本待机的情感动作尚未播完),等它结束再回休息态/排程,避免中途复位。
        // Spine 走原有逻辑,不受此守卫影响。
        if (OneShotIdle && _renderer?.IsMotionPlaying == true)
            return;
        if (_idleNames.Contains(motionName, StringComparer.OrdinalIgnoreCase))
        {
            // Live2D 待机一次性播完:平滑回到休息形态,间隔从此刻(动作结束)开始计。
            // Spine 待机循环播放,其每圈完成也走这里,但 ResetToRest 对 Spine 是空操作、也不重排程。
            _renderer?.ResetToRest();
            if (OneShotIdle)
                ScheduleIdle();
            return;
        }
        PlayIdle(); // 一次性动作(情感/随机)播完回待机
    }

    // ───────── 点击行为 ─────────

    private void OnModelClicked(string button)
    {
        if (button == "right")
        {
            ExecuteClickAction(RightClickAction);
            return;
        }
        if (button != "left")
            return; // 中键暂不响应

        var now = Environment.TickCount64;
        if (now - _lastLeftClickMs <= DoubleClickMs)
        {
            // 双击:取消已排队的单击
            _clickTimer?.Stop();
            _pendingSingleClick = false;
            ExecuteClickAction(DoubleClickAction);
        }
        else
        {
            _pendingSingleClick = true;
            _clickTimer?.Stop();
            _clickTimer?.Start();
        }
        _lastLeftClickMs = now;
    }

    private void ExecuteClickAction(PetClickAction action)
    {
        switch (action)
        {
            case PetClickAction.RandomMotion:
                TriggerRandomMotion();
                break;
            case PetClickAction.Idle:
                PlayIdle();
                break;
            case PetClickAction.OpenChat:
                OpenChatRequested?.Invoke();
                break;
        }
    }

    // ───────── 消息链:本插件不再自行扫描流式文本;情感 tag 由 TTS 解析后经 TriggerEmotion 驱动 ─────────
}
