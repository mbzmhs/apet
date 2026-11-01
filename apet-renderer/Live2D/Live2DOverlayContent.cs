using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;

namespace APet.Renderer.Live2D;

/// <summary>
/// Live2D 的 OverlayWindow 渲染内容:GL 渲染器 + 视图状态(Zoom/Pan/镜像),
/// 每帧把模型画进 SSAA 离屏 FBO;鼠标坐标反算模型单位坐标并做命中区域测试。
/// </summary>
public sealed class Live2DOverlayContent : IOverlayContent
{
    readonly Live2DGLRenderer _renderer = new();

    public Live2DStage Stage { get; } = new();

    /// <summary>水平镜像。</summary>
    public bool FlipX { get; set; }
    /// <summary>垂直镜像。</summary>
    public bool FlipY { get; set; }

    public float Zoom { get => _zoom; set => _zoom = Math.Clamp(value, 0.01f, 100f); }
    float _zoom = 1f;
    public float PanX { get; set; }
    public float PanY { get; set; }

    /// <summary>视图变化(缩放/平移)时通知(嵌入面板同步 UI 用;悬浮窗模式无订阅者)。</summary>
    public event Action? ViewChanged;

    /// <summary>在模型像素上按下鼠标(Handled = 阻止窗口默认拖动/点击判定)。</summary>
    public event Action<OverlayModelMouseEventArgs>? ModelMouseDown;
    /// <summary>在模型像素上释放鼠标。</summary>
    public event Action<OverlayModelMouseEventArgs>? ModelMouseUp;
    /// <summary>鼠标在模型像素上移动。</summary>
    public event Action<OverlayModelMouseEventArgs>? ModelMouseMove;
    /// <summary>点击模型。</summary>
    public event Action<OverlayModelMouseEventArgs>? ModelClick;

    public bool IsModelLoaded => Stage.IsLoaded;

    public void OnGLReady() => _renderer.Init();

    public void Update(double delta) => Stage.Update((float)delta);

    public void Render(int width, int height, float supersample)
    {
        _renderer.BeginFrame(width, height, Zoom * supersample, PanX, PanY);
        if (Stage.Model != null)
            _renderer.DrawModel(Stage.Model, Stage.ModelOpacity, FlipX, FlipY);
    }

    /// <summary>加载 Live2D 模型(model3.json 路径);须经 OverlayWindow.RunOnGL 在 GL 上下文内调用。</summary>
    public void LoadModel(string model3Path)
        => Stage.Load(model3Path, _renderer.LoadTexture);

    /// <summary>卸载当前模型,释放 GPU 纹理与模型内存。之后可再次 LoadModel;须经 RunOnGL 调用。</summary>
    public void UnloadModel()
    {
        _renderer.ClearDrawables();
        _renderer.DisposeTextures();
        Stage.Dispose();
    }

    /// <summary>把客户区像素坐标反算为模型单位坐标并做命中区域测试。</summary>
    OverlayModelMouseEventArgs BuildMouseArgs(MouseEventArgs e, int w, int h)
    {
        float mx = PanX + (e.X - w / 2f) / Zoom;
        float my = PanY + (h / 2f - e.Y) / Zoom;
        return new OverlayModelMouseEventArgs(e.Button, mx, my, Stage.HitTest(mx, my));
    }

    public bool MouseDown(MouseEventArgs e, int width, int height)
    {
        var args = BuildMouseArgs(e, width, height);
        ModelMouseDown?.Invoke(args);
        return args.Handled;
    }

    public bool MouseUp(MouseEventArgs e, int width, int height)
    {
        var args = BuildMouseArgs(e, width, height);
        ModelMouseUp?.Invoke(args);
        if (!args.Handled)
            ModelClick?.Invoke(args);
        return args.Handled;
    }

    public void MouseMove(MouseEventArgs e, int width, int height)
        => ModelMouseMove?.Invoke(BuildMouseArgs(e, width, height));

    public void Wheel(float zoomFactor)
    {
        Zoom *= zoomFactor; // setter 内钳制 0.01-100
        ViewChanged?.Invoke();
    }

    public void MiddleDrag(int dx, int dy) { }

    /// <summary>按模型包围盒把视图复位到适配窗口(嵌入面板用;宠物模式走 ComputeWindowSize)。</summary>
    public void ResetView(int clientWidth, int clientHeight)
    {
        if (Stage.Model == null) return;
        var (minX, minY, maxX, maxY) = Stage.GetModelBounds();
        float w = Math.Max(maxX - minX, 1f), h = Math.Max(maxY - minY, 1f);
        Zoom = Math.Min(clientWidth * 0.9f / w, clientHeight * 0.9f / h);
        PanX = (minX + maxX) / 2f;
        PanY = (minY + maxY) / 2f;
        ViewChanged?.Invoke();
    }

    /// <summary>按模型包围盒 × 缩放因子计算窗口客户区尺寸,并把 Zoom/Pan 对准包围盒中心。
    /// 取用户缩放与"适配屏幕 90%"中较小者,余量防止动画超出初始包围盒时被边缘裁剪。</summary>
    public System.Drawing.Size ComputeWindowSize(float scaleFactor)
    {
        if (Stage.Model == null)
            return new System.Drawing.Size(700, 700);
        var (minX, minY, maxX, maxY) = Stage.GetModelBounds();
        float bw = Math.Max(maxX - minX, 1f), bh = Math.Max(maxY - minY, 1f);

        var wa = SystemInformation.WorkingArea;
        float fitScale = Math.Min(wa.Width * 0.9f / bw, wa.Height * 0.9f / bh);
        float scale = MathF.Max(0.01f, Math.Min(scaleFactor, fitScale));

        const float margin = 1.15f;
        int w = Math.Max(8, (int)MathF.Ceiling(bw * scale * margin));
        int h = Math.Max(8, (int)MathF.Ceiling(bh * scale * margin));
        Zoom = scale;
        PanX = (minX + maxX) / 2f;
        PanY = (minY + maxY) / 2f;
        ViewChanged?.Invoke();
        return new System.Drawing.Size(w, h);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        Stage.Dispose();
    }
}
