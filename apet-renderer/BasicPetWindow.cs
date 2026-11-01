using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace APet.Renderer;

/// <summary>
/// 基础引擎悬浮窗:无边框透明 WPF 窗体,显示单张 png(静态)或 gif 的当前帧。
/// - 透明像素经 WM_NCHITTEST 上报 HTTRANSPARENT,实现与 Spine 悬浮窗一致的逐像素鼠标穿透;
///   不透明像素区域可点击/拖动,位移小于系统拖拽阈值视为点击,否则为拖动并上报新坐标(物理像素);
/// - 窗体在 WPF UI 线程创建,同线程多实例互不干扰(基础引擎不使用 OpenGL,无上下文限制);
/// - GIF 动画帧推进由 BasicPetRenderer 驱动(本窗体只负责呈现与交互)。
/// </summary>
public sealed class BasicPetWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private readonly Grid _grid = new();
    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    // 过渡层:切换动作时短暂显示旧图并淡出,露出下方的新图,形成交叉淡入淡出
    private readonly Image _overlay = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        IsHitTestVisible = false,
    };

    private byte[]? _alpha;    // 当前显示帧的 alpha 通道(图像素分辨率,行优先),命中测试用
    private int _alphaW;
    private int _alphaH;
    private float _scale = 1f; // 图像素 → DIU 的显示倍率(窗体 DIU 尺寸 = 图像素 × scale)

    // 窗体已连接呈现源时的 DPI 变换矩阵;未连接(尚未 Show)时为单位阵(SetLocationPhysical 会在 Show 后重新调用)
    private System.Windows.Media.Matrix ToDevice =>
        ((HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;

    private System.Windows.Media.Matrix FromDevice =>
        ((HwndSource?)PresentationSource.FromVisual(this))?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;

    private bool _dragging;
    private Point _downScreen;   // 按下时鼠标屏幕物理像素(判定点击 vs 拖动的总位移)
    private Point _lastScreen;   // 上次 MouseMove 的鼠标屏幕物理像素(拖动增量)
    private Point _downLeftTop;  // 按下时窗体 Left/Top(DIU),判定位置是否变化

    /// <summary>图片不透明像素被点击(button:left/right/middle);拖动不触发。</summary>
    public event Action<string>? Click;

    /// <summary>拖动结束后窗体左上角屏幕坐标(物理像素),宿主持久化用。</summary>
    public event Action<int, int>? PositionMoved;

    public BasicPetWindow()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        // 大倍率缩小显示立绘时用 Fant 保持高质量(默认双线性会产生锯齿)
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Fant);
        RenderOptions.SetBitmapScalingMode(_overlay, BitmapScalingMode.Fant);
        _grid.Children.Add(_image);
        _grid.Children.Add(_overlay);
        Content = _grid;

        _image.MouseEnter += (_, _) => Cursor = Cursors.Hand;
        _image.MouseLeave += (_, _) => Cursor = Cursors.Arrow;

        MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _downLeftTop = new Point(Left, Top);
            _downScreen = _lastScreen = PointToScreen(e.GetPosition(this));
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            if (!_dragging)
                return;
            var now = PointToScreen(e.GetPosition(this));
            var delta = FromDevice.Transform(new Point(now.X - _lastScreen.X, now.Y - _lastScreen.Y));
            Left += delta.X;
            Top += delta.Y;
            _lastScreen = now;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging)
                return;
            _dragging = false;
            ReleaseMouseCapture();
            EndDrag(e, "left");
        };
        MouseRightButtonUp += (_, e) => EndDrag(e, "right");
        // 右键不拖动,但按下时也要记录基准坐标,否则释放时与旧值比较会误判为"拖动"而不触发点击
        MouseRightButtonDown += (_, e) => _downScreen = PointToScreen(e.GetPosition(this));
    }

    /// <summary>
    /// 左/右键释放:与【按下时】的鼠标屏幕坐标比总位移——小于系统拖拽阈值 → 点击;
    /// 否则若窗体位置确有变化 → 上报新坐标。(不能与最后一次 Move 比:拖动结束时鼠标相对
    /// 最后一次 Move 几乎没动,恒会误判成点击,导致 PositionMoved 永不上报、位置无法持久化。)
    /// </summary>
    private void EndDrag(MouseButtonEventArgs e, string button)
    {
        var screen = PointToScreen(e.GetPosition(this));
        var totalDiu = FromDevice.Transform(new Point(screen.X - _downScreen.X, screen.Y - _downScreen.Y));
        var minX = SystemParameters.MinimumHorizontalDragDistance;
        var minY = SystemParameters.MinimumVerticalDragDistance;
        if (Math.Abs(totalDiu.X) < minX && Math.Abs(totalDiu.Y) < minY)
        {
            Click?.Invoke(button);
        }
        else if (button == "left" && (Left != _downLeftTop.X || Top != _downLeftTop.Y))
        {
            var physical = ToDevice.Transform(new Point(Left, Top));
            PositionMoved?.Invoke((int)physical.X, (int)physical.Y);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
    }

    // 逐像素命中:透明像素把鼠标消息让给下层窗口(WM_NCHITTEST → HTTRANSPARENT)
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && IsTransparentAt(lParam))
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    private bool IsTransparentAt(IntPtr lParam)
    {
        if (_alpha is null)
            return true; // 尚未显示任何图片:整窗穿透
        var sx = (short)((long)lParam & 0xFFFF);
        var sy = (short)(((long)lParam >> 16) & 0xFFFF);
        var origin = PointToScreen(new Point(0, 0)); // 窗体左上角屏幕物理像素
        var dpi = ToDevice.M11;
        if (dpi <= 0)
            dpi = 1;
        // 物理位移 → 窗内 DIU(/dpi)→ 图像素(/scale)
        var ix = (int)((sx - origin.X) / (dpi * _scale));
        var iy = (int)((sy - origin.Y) / (dpi * _scale));
        if (ix < 0 || iy < 0 || ix >= _alphaW || iy >= _alphaH)
            return true;
        return _alpha[iy * _alphaW + ix] < 10;
    }

    /// <summary>
    /// 显示一张图并按其尺寸 × scale 设定窗体大小(切换动作/首帧时调用)。
    /// fade 开启时旧图在过渡层淡出,与新图形成交叉淡入淡出。
    /// </summary>
    public void ShowFrame(BitmapSource frame, int pixelW, int pixelH, float scale, bool fade, int fadeMs)
    {
        _scale = scale;
        if (fade && _image.Source is BitmapSource old)
        {
            _overlay.Source = old;
            _overlay.Opacity = 1;
            var animation = new System.Windows.Media.Animation.DoubleAnimation(0, new System.Windows.Duration(TimeSpan.FromMilliseconds(Math.Max(1, fadeMs))))
            {
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            };
            animation.Completed += (_, _) => _overlay.Source = null;
            _overlay.BeginAnimation(OpacityProperty, animation);
        }
        else if (!fade)
        {
            // 显式关闭过渡(或单次切换):清掉可能残留的过渡层
            _overlay.BeginAnimation(OpacityProperty, null);
            _overlay.Source = null;
        }
        UpdateAlpha(frame);
        Width = pixelW * scale;
        Height = pixelH * scale;
        _image.Source = frame;
    }

    /// <summary>同一动作内的后续帧:只换图,窗体尺寸不变。</summary>
    public void UpdateFrame(BitmapSource frame)
    {
        UpdateAlpha(frame);
        _image.Source = frame;
    }

    /// <summary>按新缩放因子重设窗体大小(当前显示内容不变)。</summary>
    public void Resize(int pixelW, int pixelH, float scale)
    {
        _scale = scale;
        Width = pixelW * scale;
        Height = pixelH * scale;
    }

    /// <summary>设置窗体左上角屏幕坐标(入参为物理像素,与 Spine 悬浮窗的坐标语义一致)。</summary>
    public void SetLocationPhysical(int x, int y)
    {
        var diu = FromDevice.Transform(new Point(x, y));
        Left = diu.X;
        Top = diu.Y;
    }

    /// <summary>窗体屏幕包围盒(左上角 + 尺寸,物理像素);供气泡锚定。</summary>
    public System.Drawing.Rectangle PhysicalBounds
    {
        get
        {
            var tl = ToDevice.Transform(new Point(Left, Top));
            var br = ToDevice.Transform(new Point(Left + Width, Top + Height));
            return new System.Drawing.Rectangle((int)tl.X, (int)tl.Y, (int)(br.X - tl.X), (int)(br.Y - tl.Y));
        }
    }

    private void UpdateAlpha(BitmapSource frame)
    {
        try
        {
            _alphaW = frame.PixelWidth;
            _alphaH = frame.PixelHeight;
            var stride = _alphaW * 4;
            var buffer = new byte[stride * _alphaH];
            frame.CopyPixels(buffer, stride, 0);
            _alpha = new byte[_alphaW * _alphaH];
            for (var i = 0; i < _alpha.Length; i++)
                _alpha[i] = buffer[i * 4 + 3];
        }
        catch
        {
            _alpha = null; // 像素读取失败时退化为整窗命中
        }
    }
}
