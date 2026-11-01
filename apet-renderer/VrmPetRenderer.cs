using APet.Renderer.Vrm;

namespace APet.Renderer;

/// <summary>
/// VRM 引擎渲染器:VrmStage(骨骼/表情/蒙皮/弹簧骨/VRMA 动画)+ OverlayWindow(ULW 逐像素透明悬浮窗)。
/// "动作" 命名空间 = VRM 表情 key(情绪 preset 与自定义)+ 已导入的 VRMA 动作(名=文件基名):
/// 表情播放即拉满其权重、其余情绪类淡出;VRMA 播放走 stage.PlayAnimation(期间暂停内建待机,播完回待机)。
/// 情绪触发时表情与动作可同时生效(见 TriggerEmotion)。眨眼/呼吸/环视为内建待机。
/// </summary>
public sealed class VrmPetRenderer : PetRendererBase
{
    private readonly VrmOverlayContent _content = new();
    private VrmStage? _stage;
    private OverlayWindow? _view;

    private List<string> _animPaths = new();
    private readonly Dictionary<string, VrmAnimation> _animations = new(StringComparer.OrdinalIgnoreCase);

    public override string Engine => "vrm";
    public override string EngineDisplayName => "VRM";

    public override bool IsLoaded => _stage is not null;
    public override string? CurrentMotion => _stage?.CurrentAnimationName;

    /// <summary>是否有 VRMA 动作在播(表情/待机不计)。</summary>
    public override bool IsMotionPlaying => _stage?.IsAnimationPlaying ?? false;

    protected override string? LoadCore(string modelPath, string? atlasPath)
    {
        var model = VrmLoader.Load(modelPath);
        var stage = new VrmStage(model);
        stage.AnimationCompleted += RaiseMotionCompleted;

        EnsureView();
        _content.SetShading(_shadingMode); // SetShading 可能早于建窗调用,此处补应用
        _content.SetStage(stage); // 先设模型(FitToModel 依赖),窗口尺寸与相机随之匹配
        _view!.ScaleFactor = _scale;
        _view.FitToModel();
        _stage = stage;
        RebuildAnimations(); // 模型就绪后按已存路径建 VRMA 表(可能先于 SetAnimations 调用)
        return null;
    }

    /// <summary>创建悬浮窗(置顶、不进任务栏);VRM 可重复 Show(已建窗时直接复现)。</summary>
    private void EnsureView()
    {
        if (_view is not null)
        {
            _view.Show();
            return;
        }
        _view = new OverlayWindow(new OverlayOptions
        {
            FrameMs = 8,          // 尽快提交渲染,实际帧率由垂直同步钉在显示器刷新率
            DepthBuffer = true,   // 3D 渲染需要深度
            UseVSync = true,
            MiddleDragOrbit = true,
            DiagLogName = "vrm-overlay-diag.log",
            InitialSize = new System.Drawing.Size(600, 900),
        })
        {
            Content = _content,
            TopMost = true,
        };
        if (WindowTitle is not null)
            _view.Text = WindowTitle;
        _view.Location = new System.Drawing.Point(_posX, _posY);
        _view.PositionMoved += (x, y) => RaisePositionMoved(x, y);
        _view.ModelClick += RaiseModelClicked;
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
        HideView();
        _stage = null;
        LoadError = null;
    }

    /// <summary>可用"动作" = VRM 表情 key(情绪/自定义)∪ 已导入 VRMA 动作名(文件基名)。</summary>
    public override string[] GetMotionNames()
    {
        var exprs = _stage?.GetEmotionKeys() ?? Array.Empty<string>();
        return exprs.Concat(_animations.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public override PetMotionInfo[] GetMotionInfos() =>
        GetMotionNames().Select(name => new PetMotionInfo
        {
            Name = name,
            Detail = _animations.TryGetValue(name, out var clip) ? $"VRMA 动作  ({clip.Duration:F1}s)" : name,
        }).ToArray();

    /// <summary>设置 VRMA 路径列表(过滤不存在的);已加载模型则立即重建动画表,否则待 Load 时建。</summary>
    public override void SetAnimations(IReadOnlyList<string> animationPaths)
    {
        _animPaths = animationPaths.Where(File.Exists).ToList();
        if (_stage is not null)
            RebuildAnimations();
    }

    private void RebuildAnimations()
    {
        _animations.Clear();
        foreach (var p in _animPaths)
        {
            var clip = VrmAnimationLoader.Load(p);
            if (clip is not null)
                _animations[Path.GetFileNameWithoutExtension(p)] = clip;
        }
    }

    public override void Play(string motionName, bool loop)
    {
        if (_stage is null)
            return;
        // 命中 VRMA 动作 → 播放动画(期间 stage 自动暂停内建待机);否则按表情处理
        if (_animations.TryGetValue(motionName, out var clip))
        {
            _stage.PlayAnimation(clip, loop);
            return;
        }
        // 表情 = 情绪类互斥切换:目标 key 拉满,其余情绪/自定义归零;眨眼口型不受影响
        foreach (var key in _stage.ExpressionKeys)
        {
            if (key.Equals(motionName, StringComparison.OrdinalIgnoreCase))
                _stage.SetExpression(key, 1f);
            else if (_stage.GetEmotionKeys().Contains(key, StringComparer.OrdinalIgnoreCase))
                _stage.SetExpression(key, 0f);
        }
    }

    /// <summary>情绪触发:把候选名拆成「表情」与「VRMA 动作」两类,各随机取一个,同时生效。</summary>
    public override void TriggerEmotion(IReadOnlyList<string> names)
    {
        if (_stage is null || names.Count == 0)
            return;
        var exprs = new List<string>();
        var anims = new List<string>();
        foreach (var n in names)
        {
            if (_animations.ContainsKey(n))
                anims.Add(n);
            else if (_stage.ExpressionKeys.Contains(n, StringComparer.OrdinalIgnoreCase))
                exprs.Add(n);
        }
        if (exprs.Count > 0)
            Play(exprs[Random.Shared.Next(exprs.Count)], false); // 表情分支(Play 内互斥切换)
        if (anims.Count > 0)
            _stage.PlayAnimation(_animations[anims[Random.Shared.Next(anims.Count)]], false);
    }

    protected override void OnScaleApplied()
    {
        if (_view is not null && _stage is not null)
        {
            _view.ScaleFactor = _scale;
            _view.FitToModel();
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

    private int _shadingMode = 2; // 0=toon 1=pbr 2=mtoon(官方)
    public override void SetShading(int mode)
    {
        _shadingMode = Math.Clamp(mode, 0, 2);
        _content.SetShading(_shadingMode);
    }

    public override void Dispose()
    {
        if (_view is not null)
        {
            // 窗体 Dispose 内部会 MakeCurrent 并释放自己的 GL 资源(content:渲染器/离屏缓冲)
            _view.Close();
            _view.Dispose();
            _view = null;
            _viewShown = false;
        }
        _stage = null;
    }
}
