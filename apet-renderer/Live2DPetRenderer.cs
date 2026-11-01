using APet.Renderer.Live2D;
using System.Windows.Forms;

namespace APet.Renderer;

/// <summary>
/// Live2D 引擎渲染器:Live2DOverlayContent(Live2DStage + GL 渲染)+ OverlayWindow(逐像素透明悬浮窗)。
/// 透明像素自动鼠标穿透,模型像素可点击/拖动;窗口置顶、不进任务栏。
///
/// 命名空间约定(与 Spine 的关键差异):
/// Live2D 的情感差分靠"表情"(exp3.json / 自定义参数预设,均为参数级叠层)而非动作,
/// 因此本渲染器把动作与表情合并进同一个名字空间,Play() 按名称路由:
/// - 动作规范名 = 原始名(def.Name ?? 文件名);跨组重名时加 "组:" 前缀(如 Idle:m01);
/// - 表情原名直接入列(动作名必含 ":" 或带前缀,结构上不冲突;若与动作重名,动作为准)。
///   表情 = 内置 exp3 + 自定义预设(用户捏的参数组合,存于 model3.json 同目录
///   &lt;模型名&gt;.customexp.json,Stage.Load 自动加载)。
/// emotionMap 应把情感 tag 优先映射到表情名(如 {"happy":["happy"]});模型无表情时映射动作名亦可,
/// idleMotions 填动作名。
///
/// 播放语义:
/// - Play(动作, loop) 与 Spine 一致:Force 优先级打断;非循环播完触发 MotionCompleted,插件层回待机;
/// - Play(表情) 不占动作轨:循环待机在底层继续播,表情叠在其上(共享参数上表情胜出),
///   保持 <see cref="ExpressionHoldMs"/> 后自动淡出。表情清除时**不触发** MotionCompleted——
///   否则插件会 PlayIdle() 重启正在播放的一次性动作;内建自动眨眼(Stage.AutoEyeBlink)
///   使 blinkMotion 设置项对 Live2D 无用,宿主应留空。
/// </summary>
public sealed class Live2DPetRenderer : PetRendererBase
{
    /// <summary>表情触发后的保持时长(毫秒),到期自动淡出;重复触发同一/不同表情会刷新计时。</summary>
    private const int ExpressionHoldMs = 3000;

    private readonly Live2DOverlayContent _content = new();
    private OverlayWindow? _view;

    private readonly List<(string Canonical, string Group, string Name, float Duration)> _motionInfos = new();
    private readonly Dictionary<string, (string Group, string Name)> _motionLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rawToCanonical = new(StringComparer.OrdinalIgnoreCase);

    private System.Windows.Forms.Timer? _expressionTimer;

    public override string Engine => "live2d";
    public override string EngineDisplayName => "Live2D";

    public override bool IsLoaded => _content.IsModelLoaded;
    public override string? CurrentMotion => _viewShown || IsLoaded
        ? (_content.Stage.CurrentMotionName is { } raw ? _rawToCanonical.GetValueOrDefault(raw, raw) : null)
        : null;

    /// <summary>是否仍有动作在播(含循环待机)。</summary>
    public override bool IsMotionPlaying => _content.Stage.IsMotionPlaying;

    /// <summary>已加载模型的 Live2DStage(宿主开放捏表情等精细编辑用;未加载为 null)。</summary>
    public Live2DStage? Stage => IsLoaded ? _content.Stage : null;

    // 兜底部件不透明度(默认形态):Load 完成后应用。多形态角色(如两双手臂)借此隐藏默认形态;
    // 自定义表情淡出会精确还原到"应用前状态"——即该基线,而非 setup 姿态,因此任何时刻都不露馅。
    private IReadOnlyDictionary<string, float> _partOpacities = new Dictionary<string, float>();

    // 内建眨眼/呼吸开关(用户设定):Load 会按环境变量重置 AutoEyeBlink,加载完成后须恢复
    private bool _autoEyeBlink = true;
    private bool _breathEnabled = true;

    /// <summary>设置兜底部件不透明度。模型已加载时立即应用;否则记住,加载完成后应用。</summary>
    public override void SetPartOpacities(IReadOnlyDictionary<string, float> opacities)
    {
        _partOpacities = opacities ?? new Dictionary<string, float>();
        ApplyPartOpacities();
    }

    private void ApplyPartOpacities()
    {
        if (!IsLoaded)
            return;
        foreach (var (id, opacity) in _partOpacities)
            _content.Stage.SetPartOpacity(id, opacity);
    }

    /// <summary>回到休息形态(平滑过渡):参数混回默认、部件混回「setup + 兜底」基线。待机一次性播完时由插件层调用。</summary>
    public override void ResetToRest()
    {
        var stage = _content.Stage;
        if (!stage.IsLoaded)
            return;
        // 0.6s 正弦缓动回位(硬复位会跳变僵硬);兜底基线作为部件目标,多形态角色待机结束后仍隐藏默认形态
        stage.ResetToRest(0.6f, _partOpacities);
    }

    /// <summary>内建自动眨眼开关。已加载立即生效;否则记住,Load 完成后恢复。</summary>
    public override void SetAutoEyeBlink(bool enabled)
    {
        _autoEyeBlink = enabled;
        if (IsLoaded)
            _content.Stage.AutoEyeBlink = enabled;
    }

    /// <summary>呼吸动画开关。已加载立即生效;否则记住,Load 完成后恢复。</summary>
    public override void SetBreathEnabled(bool enabled)
    {
        _breathEnabled = enabled;
        if (IsLoaded)
            _content.Stage.BreathEnabled = enabled;
    }

    protected override string? LoadCore(string modelPath, string? atlasPath)
    {
        // atlasPath 对 Live2D 无意义(资源按 model3.json 的 FileReferences 自动解析),忽略。
        // 必须先建窗:OverlayWindow.OnLoad 会 MakeCurrent 建立 GL 上下文,
        // 而 stage.Load 内部上传纹理依赖当前线程已有上下文(与 Spine 同理)。
        EnsureView();
        // 先写缩放因子再加载:FitToModel 直接按用户因子定窗口大小
        _view!.ScaleFactor = _scale;
        _view.RunOnGL(() => _content.LoadModel(modelPath));
        _view.FitToModel();
        BuildNameTables();
        ApplyPartOpacities(); // 兜底形态:加载完成后应用部件基线
        // Load 内部会按环境变量重置 AutoEyeBlink,这里恢复用户设定(呼吸一并恢复保持一致)
        _content.Stage.AutoEyeBlink = _autoEyeBlink;
        _content.Stage.BreathEnabled = _breathEnabled;
        return null;
    }

    /// <summary>加载成功后建立 规范名↔(组,动作名) 映射与表情名集合。</summary>
    private void BuildNameTables()
    {
        var stage = _content.Stage;
        var infos = stage.GetMotionInfos();

        // 跨组重名检测:重名的动作规范名加 "组:" 前缀,保证名字空间无歧义
        var rawCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, name, _) in infos)
            rawCounts[name] = rawCounts.GetValueOrDefault(name) + 1;

        _motionInfos.Clear();
        _motionLookup.Clear();
        _rawToCanonical.Clear();
        foreach (var (group, name, duration) in infos)
        {
            string canonical = rawCounts[name] > 1 ? group + ":" + name : name;
            _motionInfos.Add((canonical, group, name, duration));
            _motionLookup[canonical] = (group, name);
            if (!_rawToCanonical.ContainsKey(name))
                _rawToCanonical[name] = canonical;   // 重名时先到组优先(罕见边界,事件反查用)
        }
    }

    /// <summary>
    /// 表情名实时读取(而非加载时缓存):自定义表情可能在模型加载后经捏表情窗口
    /// 新增或删除,缓存的名单会让新表情不出现在动作列表、也无法被 Play 路由到。
    /// </summary>
    private HashSet<string> CurrentExpressionNames()
    {
        var stage = _content.Stage;
        return !stage.IsLoaded
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(stage.GetExpressionNames(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>创建悬浮窗(置顶、不进任务栏);首次成功加载模型时显示。</summary>
    private void EnsureView()
    {
        if (_view is not null)
            return;
        _view = new OverlayWindow(new OverlayOptions())
        {
            Content = _content,
            TopMost = true,
        };
        if (WindowTitle is not null)
            _view.Text = WindowTitle;
        // 用已持久化的坐标定位(宿主在 Load 前经 SetPosition 写入);避免多宠物叠同处、也避免从默认坐标闪现再跳
        _view.Location = new System.Drawing.Point(_posX, _posY);
        _view.PositionMoved += (x, y) => RaisePositionMoved(x, y);
        _view.ModelClick += button => RaiseModelClicked(button);
        // 悬停提示:鼠标在模型像素上时显示手型光标(ULW 逐像素命中,透明处收不到事件、离开自动恢复)
        var view = _view;
        view.ModelMouseMove += _ =>
        {
            if (view.Cursor != Cursors.Hand)
                view.Cursor = Cursors.Hand;
        };
        view.MouseLeave += (_, _) =>
        {
            if (view.Cursor != Cursors.Default)
                view.Cursor = Cursors.Default;
        };
        // 动作播完(含被打断淡出完毕):原始名反查规范名后上报,插件层据此回待机/恢复眨眼
        _content.Stage.OnMotionEnd += rawName =>
            RaiseMotionCompleted(_rawToCanonical.GetValueOrDefault(rawName, rawName));
        _view.Show();
        _viewShown = true;
    }

    protected override void HideViewCore()
    {
        if (_view is not null)
            _view.Hide();
    }

    public override void Unload()
    {
        StopExpressionTimer();
        HideView();
        // 在其 GL 上下文下释放模型与纹理(之后可再次 Load)
        _view?.RunOnGL(() => _content.UnloadModel());
        LoadError = null;
    }

    public override string[] GetMotionNames()
    {
        if (!IsLoaded)
            return [];
        var names = new List<string>(_motionInfos.Count + 8);
        foreach (var (canonical, _, _, _) in _motionInfos)
            names.Add(canonical);
        names.AddRange(CurrentExpressionNames());
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.ToArray();
    }

    public override PetMotionInfo[] GetMotionInfos()
    {
        if (!IsLoaded)
            return [];
        var list = new List<PetMotionInfo>(_motionInfos.Count + 8);
        foreach (var (canonical, group, _, duration) in _motionInfos)
            list.Add(new PetMotionInfo { Name = canonical, Detail = $"{canonical}  [动作·{group}]  ({duration:F2}s)" });
        var stage = _content.Stage;
        var custom = stage.GetCustomExpressionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in CurrentExpressionNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            // 自定义表情(用户捏的参数预设,sidecar 自动加载)与内置 exp3 表情区分标注
            var tag = custom.Contains(name) ? "[自定义表情]" : "[表情]";
            list.Add(new PetMotionInfo { Name = name, Detail = $"{name}  {tag}" });
        }
        return list.ToArray();
    }

    public override void Play(string motionName, bool loop)
    {
        var stage = _content.Stage;
        if (!stage.IsLoaded)
            return;

        if (_motionLookup.TryGetValue(motionName, out var target))
        {
            // 身体动作与表情计时器互不干扰:已生效的表情保持到期后照常淡出(叠在动作之上)
            stage.PlayMotion(target.Group, target.Name, Live2DStage.MotionPriority.Force, loop);
            return;
        }
        if (CurrentExpressionNames().Contains(motionName))
        {
            // 表情 = 参数级叠层:不占动作轨,底层待机继续播;保持一段时间后自动淡出
            stage.SetExpression(motionName);
            StartExpressionTimer();
        }
    }

    /// <summary>启动/刷新表情保持计时器;到期清除表情(不触发 MotionCompleted,见类注释)。</summary>
    private void StartExpressionTimer()
    {
        if (_expressionTimer is null)
        {
            _expressionTimer = new System.Windows.Forms.Timer { Interval = ExpressionHoldMs };
            _expressionTimer.Tick += (_, _) =>
            {
                _expressionTimer?.Stop();
                _content.Stage.SetExpression(null);
            };
        }
        _expressionTimer.Stop();
        _expressionTimer.Interval = ExpressionHoldMs;
        _expressionTimer.Start();
    }

    private void StopExpressionTimer() => _expressionTimer?.Stop();

    protected override void OnScaleApplied()
    {
        if (_view is not null && IsLoaded)
        {
            _view.ScaleFactor = _scale;
            _view.FitToModel();   // 实时按新因子重算窗口大小并居中
        }
    }

    protected override void OnPositionApplied()
    {
        if (_view is not null)
            _view.Location = new System.Drawing.Point(_posX, _posY);
    }

    protected override void OnTitleApplied()
    {
        if (_view is not null)
            _view.Text = WindowTitle;
    }

    protected override System.Drawing.Rectangle ViewBounds => _view?.Bounds ?? System.Drawing.Rectangle.Empty;

    public override void Dispose()
    {
        StopExpressionTimer();
        _expressionTimer?.Dispose();
        _expressionTimer = null;
        if (_view is not null)
        {
            // 窗口的 Dispose 会在自身 GL 上下文下释放 content(stage、纹理与离屏资源)
            _view.Close();
            _view.Dispose();
            _view = null;
            _viewShown = false;
        }
    }
}
