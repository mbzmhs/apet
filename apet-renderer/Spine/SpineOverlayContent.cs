using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;

namespace APet.Renderer.Spine;

/// <summary>
/// Spine 的 OverlayWindow 渲染内容:GL 渲染器 + 视图状态(Zoom/Pan/调试叠加),
/// 每帧把骨架画进 SSAA 离屏 FBO;鼠标坐标反算骨架坐标系供宿主做命中响应。
/// </summary>
public sealed class SpineOverlayContent : IOverlayContent
{
    readonly SpineGLRenderer _renderer = new();

    public SpineStage Stage { get; } = new();

    public bool ShowBones { get; set; }
    public bool ShowBounds { get; set; }

    public float Zoom { get; set; } = 1f;
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

    public void OnGLReady() => _renderer.Init();

    public void Update(double delta) => Stage.Update((float)delta);

    public void Render(int width, int height, float supersample)
    {
        _renderer.BeginFrame(width, height, Zoom * supersample, PanX, PanY);
        if (Stage.Skeleton != null)
        {
            _renderer.DrawSkeleton(Stage.Skeleton);
            if (ShowBones || ShowBounds)
                _renderer.DrawDebug(Stage.Skeleton, ShowBones, ShowBounds);
        }
    }

    /// <summary>把客户区像素坐标反算为骨架坐标系(与 BeginFrame 的正交相机互逆;y 向上)。</summary>
    OverlayModelMouseEventArgs BuildMouseArgs(MouseEventArgs e, int w, int h)
    {
        float sx = PanX + (e.X - w / 2f) / Zoom;
        float sy = PanY + (h / 2f - e.Y) / Zoom;
        return new OverlayModelMouseEventArgs(e.Button, sx, sy, null);
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
        Zoom = MathF.Max(0.05f, Zoom * zoomFactor);
        ViewChanged?.Invoke();
    }

    public void MiddleDrag(int dx, int dy) { }

    /// <summary>按骨架数据把视图复位到适配窗口(嵌入面板用;宠物模式走 ComputeWindowSize)。</summary>
    public void ResetView(int clientWidth, int clientHeight)
    {
        var data = Stage.SkeletonData;
        float w = data?.Width > 0 ? data.Width : 200f;
        float h = data?.Height > 0 ? data.Height : 200f;
        Zoom = Math.Min(clientWidth * 0.9f / w, clientHeight * 0.9f / h);
        PanX = (data?.X ?? 0) + w / 2f;
        PanY = (data?.Y ?? 0) + h / 2f;
        ViewChanged?.Invoke();
    }

    /// <summary>按模型包围盒 × 缩放因子计算窗口客户区尺寸,并把 Zoom/Pan 对准包围盒中心。
    /// 取用户缩放与"适配屏幕 90%"中较小者,余量防止动画超出 setup 包围盒时被边缘裁剪。</summary>
    public System.Drawing.Size ComputeWindowSize(float scaleFactor)
    {
        var data = Stage.SkeletonData;
        if (data == null)
            return new System.Drawing.Size(700, 700);
        float bw = data.Width > 0 ? data.Width : 200f;
        float bh = data.Height > 0 ? data.Height : 200f;

        var wa = SystemInformation.WorkingArea;
        float fitScale = Math.Min(wa.Width * 0.9f / bw, wa.Height * 0.9f / bh);
        float scale = MathF.Max(0.01f, Math.Min(scaleFactor, fitScale));

        const float margin = 1.15f;
        int w = Math.Max(8, (int)MathF.Ceiling(bw * scale * margin));
        int h = Math.Max(8, (int)MathF.Ceiling(bh * scale * margin));
        Zoom = scale;
        PanX = data.X + bw / 2f;
        PanY = data.Y + bh / 2f;
        ViewChanged?.Invoke();
        return new System.Drawing.Size(w, h);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        Stage.Dispose();
    }
}
