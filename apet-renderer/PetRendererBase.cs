using System.Windows.Forms;

namespace APet.Renderer;

/// <summary>
/// 四引擎渲染器的公共基类:悬浮窗位置/缩放/标题的记忆与应用、加载守卫与统一错误格式、
/// 屏幕回退、点击/动作完成/位置变更事件,全部收敛在此;引擎只实现自身的加载、动作与窗口装配钩子。
/// </summary>
public abstract class PetRendererBase : IPetRenderer
{
    protected bool _viewShown;
    protected float _scale = 1f;
    protected int _posX = 450;
    protected int _posY = 100;
    private string? _windowTitle;

    public string? LoadError { get; protected set; }

    public abstract string Engine { get; }
    public abstract string EngineDisplayName { get; }
    public abstract bool IsLoaded { get; }
    public abstract string? CurrentMotion { get; }
    public abstract bool IsMotionPlaying { get; }

    public event Action<string>? ModelClicked;
    public event Action<string>? MotionCompleted;
    public event Action<int, int>? PositionMoved;

    protected void RaiseModelClicked(string button) => ModelClicked?.Invoke(button);
    protected void RaiseMotionCompleted(string name) => MotionCompleted?.Invoke(name);
    protected void RaisePositionMoved(int x, int y) => PositionMoved?.Invoke(x, y);

    /// <summary>窗口标题(经 SetWindowTitle 记忆;建窗时套用)。</summary>
    protected string? WindowTitle => _windowTitle;

    /// <summary>
    /// 加载模型模板:存在性守卫 → 引擎加载(LoadCore)→ 失败隐藏窗口并返回统一格式错误。
    /// modelPath 按引擎解释(Spine: .skel/.json 骨架;Live2D: .model3.json;VRM: .vrm;basic: 图片文件夹);
    /// atlasPath 仅 Spine 图集可选覆盖,其余引擎忽略。
    /// </summary>
    public string? Load(string modelPath, string? atlasPath)
    {
        if (!ModelPathExists(modelPath))
        {
            LoadError = "找不到模型文件: " + modelPath;
            return LoadError;
        }
        try
        {
            var error = LoadCore(modelPath, atlasPath);
            if (error is not null)
            {
                LoadError = error;
                HideView();
                return LoadError;
            }
            LoadError = null;
            return null;
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException is not null ? " (" + ex.InnerException.Message + ")" : "";
            LoadError = $"{EngineDisplayName} 模型加载失败: {ex.Message}{detail}";
            HideView();
            return LoadError;
        }
    }

    /// <summary>引擎加载;返回 null = 成功,否则为错误信息(基类负责落 LoadError 并隐藏窗口)。</summary>
    protected abstract string? LoadCore(string modelPath, string? atlasPath);

    /// <summary>模型路径存在性(默认文件存在;basic 引擎覆盖为目录存在)。</summary>
    protected virtual bool ModelPathExists(string path) => File.Exists(path);

    public abstract void Unload();

    public abstract string[] GetMotionNames();

    public abstract PetMotionInfo[] GetMotionInfos();

    public abstract void Play(string motionName, bool loop);

    public void SetScale(float scale)
    {
        if (scale <= 0f)
            return;
        _scale = scale;
        OnScaleApplied();
    }

    /// <summary>缩放因子变化:已建窗则套用(ScaleFactor + FitToModel);未建窗时基类已记忆。</summary>
    protected abstract void OnScaleApplied();

    public void SetPosition(int x, int y)
    {
        // 落点若已不在任何屏幕上(如拔掉了外接显示器),回退默认位置,避免宠物"消失"在屏幕外找不回
        if (!IsOnAnyScreen(x, y))
        {
            x = 450;
            y = 100;
        }
        _posX = x;
        _posY = y;
        OnPositionApplied();
    }

    /// <summary>位置变化:已建窗则立即移动;未建窗时基类已记忆。</summary>
    protected abstract void OnPositionApplied();

    public void SetWindowTitle(string title)
    {
        _windowTitle = title;
        OnTitleApplied();
    }

    /// <summary>标题变化:已建窗则立即套用。</summary>
    protected abstract void OnTitleApplied();

    /// <summary>当前悬浮窗屏幕包围盒(气泡锚定用;未建窗为空)。</summary>
    protected abstract System.Drawing.Rectangle ViewBounds { get; }

    public System.Drawing.Rectangle GetWindowBounds() => ViewBounds;

    /// <summary>隐藏悬浮窗(幂等;模型加载失败时调用,窗口保留待下次加载复用)。</summary>
    protected void HideView()
    {
        if (_viewShown)
        {
            HideViewCore();
            _viewShown = false;
        }
    }

    /// <summary>引擎隐藏窗口本体(Hide;不销毁)。</summary>
    protected abstract void HideViewCore();

    private static bool IsOnAnyScreen(int x, int y)
    {
        foreach (var s in Screen.AllScreens)
            if (x >= s.Bounds.Left && x < s.Bounds.Right && y >= s.Bounds.Top && y < s.Bounds.Bottom)
                return true;
        return false;
    }

    /// <summary>销毁渲染器与窗口(GL 资源须在各自上下文内释放)。</summary>
    public abstract void Dispose();

    // ───────── 引擎可选能力的虚实现(取代接口默认方法:接口经抽象基类继承时,
    // DIM 分发在运行时命中了默认体而非派生类实现,虚分发无此问题) ─────────

    /// <summary>情绪触发默认实现:从候选里随机挑一个播放(非循环)。</summary>
    public virtual void TriggerEmotion(IReadOnlyList<string> names)
    {
        if (names.Count > 0)
            Play(names[Random.Shared.Next(names.Count)], false);
    }

    /// <summary>VRM 专属:导入 VRMA 动画文件列表。</summary>
    public virtual void SetAnimations(IReadOnlyList<string> animationPaths) { }

    /// <summary>Live2D 专属:兜底部件不透明度基线(模型加载后应用,表情淡出回到该基线)。</summary>
    public virtual void SetPartOpacities(IReadOnlyDictionary<string, float> opacities) { }

    /// <summary>Live2D 专属:回到休息形态(一次性待机播完后的自然回位)。</summary>
    public virtual void ResetToRest() { }

    /// <summary>基础引擎专属:切换动作图片的淡入淡出过渡。</summary>
    public virtual void SetTransition(bool enabled, int durationMs) { }

    /// <summary>VRM 专属:渲染风格(0=toon 1=pbr 2=mtoon)。</summary>
    public virtual void SetShading(int mode) { }

    /// <summary>Live2D 专属:内建自动眨眼开关。</summary>
    public virtual void SetAutoEyeBlink(bool enabled) { }

    /// <summary>Live2D 专属:呼吸动画开关。</summary>
    public virtual void SetBreathEnabled(bool enabled) { }


}
