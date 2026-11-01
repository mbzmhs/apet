using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.WinForms;

namespace APet.Renderer;

/// <summary>
/// 统一的桌宠透明悬浮窗(三份引擎专属窗体 Spine/Live2D/VRM 的合并实现):
/// 无边框 + UpdateLayeredWindow 逐像素透明 —— GL 渲染到 SSAA 离屏 FBO → 降采样 →
/// 预乘 alpha BGRA 读入 DIB → ULW 上屏,由 OS 做逐像素命中(透明像素自动鼠标穿透)。
/// 引擎差异(帧步长/深度缓冲/垂直同步/诊断日志)经 <see cref="OverlayOptions"/> 表达,
/// 步进/绘制/视图状态经 <see cref="IOverlayContent"/> 注入;窗口本身只负责壳:
/// 上下文切换、离屏管线、左键拖动、点击判定、位置持久化事件、生命周期与 GPU 清理。
/// </summary>
public sealed class OverlayWindow : Form
{
    readonly GLControl _glc;
    readonly System.Windows.Forms.Timer _timer = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly OverlayOptions _options;
    private float _lastTime;
    private bool _initialized;

    // ───────── 离屏资源(SSAA 渲染目标 + 显示尺寸输出目标 + DIB) ─────────

    private int _fbo, _fboTex, _depthRbo;   // SSAA 渲染目标(大);VRM 等深度引擎挂深度渲染缓冲
    private int _outFbo, _outTex;           // 显示尺寸输出目标(小,降采样结果)
    private int _renderW, _renderH;
    private float _ssaa;
    private IntPtr _hdcMem, _hbm;
    private IntPtr _dibBits;
    private int _dibW, _dibH;
    private IntPtr _screenDc;
    private int _frameCount;
    private DateTime _lastRenderLog = DateTime.MinValue;

    // ───────── 拖动 / 中键轨道状态 ─────────

    private bool _dragging;
    private System.Drawing.Point _dragStartScreen;
    private System.Drawing.Point _downScreen;
    private System.Drawing.Point _downLocation;
    private bool _orbiting;
    private System.Drawing.Point _orbitStartScreen;
    private System.Drawing.Point _orbitDownScreen;

    public OverlayWindow(OverlayOptions options)
    {
        _options = options;
        Text = options.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(450, 100);
        ShowInTaskbar = options.ShowInTaskbar;
        ClientSize = options.InitialSize;

        _glc = new GLControl(new GLControlSettings
        {
            DepthBits = options.DepthBuffer ? 24 : 0,
            AlphaBits = 8,
            NumberOfSamples = 0,
        });
        _glc.Dock = DockStyle.Fill;
        Controls.Add(_glc);

        _glc.MouseDown += OnViewMouseDown;
        _glc.MouseUp += OnViewMouseUp;
        _glc.MouseMove += OnViewMouseMove;
        _glc.MouseWheel += OnViewMouseWheel;

        _timer.Interval = options.FrameMs;
        _timer.Tick += (_, _) => RenderFrame();
    }

    /// <summary>承载的渲染内容(引擎注入;OnLoad 时回调 OnGLReady,此后每帧 Update/Render)。</summary>
    public IOverlayContent? Content { get; set; }

    /// <summary>缩放因子(屏幕像素 / 模型单位),FitToModel 的输入;具体口径由内容实现。</summary>
    public float ScaleFactor { get; set; } = 1f;

    /// <summary>点击模型:按下与释放位移小于系统拖拽阈值的判定;参数为按键名(left/right/middle)。</summary>
    public event Action<string>? ModelClick;

    /// <summary>模型像素上的鼠标移动(ULW 已保证只有角色像素收到;可做手型光标/悬停)。</summary>
    public event Action<MouseEventArgs>? ModelMouseMove;

    /// <summary>用户拖动窗口后触发(新的左上角屏幕坐标,供宿主持久化)。</summary>
    public event Action<int, int>? PositionMoved;

    // ───────── ULW 逐像素透明(P/Invoke) ─────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED;
            return cp;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int CX, CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("opengl32.dll")]
    private static extern IntPtr wglGetProcAddress(string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool WglSwapIntervalProc(int interval);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const uint ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    // ───────── 诊断日志(按引擎文件名;未配置则关闭) ─────────

    private void Log(string msg)
    {
        var name = _options.DiagLogName;
        if (name is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APet");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, name),
                $"[{DateTime.Now:HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
        }
        catch { /* 日志失败绝不影响渲染 */ }
    }

    // ───────── 生命周期 ─────────

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Log($"OnLoad begin {ClientSize.Width}x{ClientSize.Height}");
        try
        {
            _screenDc = GetDC(IntPtr.Zero);
            _glc.MakeCurrent();
            if (_options.UseVSync)
                EnableVSync();
            Content?.OnGLReady(); // 上下文就绪:引擎在此初始化渲染器/上传静态资源
            CreateOffscreen(_glc.ClientSize.Width, _glc.ClientSize.Height);
            _initialized = true;
            _timer.Start();
            Log("OnLoad ok");
        }
        catch (Exception ex)
        {
            Log($"OnLoad FAILED: {ex}");
            throw; // 让宿主看到真实异常,而不是静默无窗口
        }
    }

    /// <summary>开启垂直同步(WGL 扩展)。取不到函数或调用失败只记日志——定时器限帧仍然生效。</summary>
    private void EnableVSync()
    {
        try
        {
            var addr = wglGetProcAddress("wglSwapIntervalEXT");
            if (addr == IntPtr.Zero)
            {
                Log("wglSwapIntervalEXT 不可用(无垂直同步,仅定时器限帧)");
                return;
            }
            var fn = Marshal.GetDelegateForFunctionPointer<WglSwapIntervalProc>(addr);
            if (!fn(1))
                Log("wglSwapIntervalEXT(1) 调用失败(仅定时器限帧)");
        }
        catch (Exception ex)
        {
            Log($"垂直同步开启失败: {ex.Message}");
        }
    }

    // ───────── 离屏管线 ─────────

    private void CreateOffscreen(int w, int h)
    {
        ReleaseOffscreen();

        _ssaa = MathF.Max(1f, _options.Supersample);
        _renderW = Math.Max(1, (int)MathF.Round(w * _ssaa));
        _renderH = Math.Max(1, (int)MathF.Round(h * _ssaa));

        // SSAA 渲染目标(大):Linear 过滤以便 glBlitFramebuffer 降采样时做双线性平均
        _fboTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fboTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, _renderW, _renderH, 0,
            PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _fboTex, 0);
        if (_options.DepthBuffer)
        {
            // 3D 渲染需要深度:离屏 FBO 必须挂深度渲染缓冲,否则不完整、绘制报 InvalidOperation
            _depthRbo = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, _renderW, _renderH);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _depthRbo);
        }

        _outTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _outTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0,
            PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _outFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _outTex, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // bottom-up DIB:首行 = 图像最底行,与 glReadPixels 行序一致,无需翻转
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = h;          // 正值 = bottom-up
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;     // BI_RGB
        _hdcMem = CreateCompatibleDC(_screenDc);
        _hbm = CreateDIBSection(_hdcMem, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        SelectObject(_hdcMem, _hbm);
        _dibW = w;
        _dibH = h;
    }

    private void ReleaseOffscreen()
    {
        if (_fbo != 0) GL.DeleteFramebuffer(_fbo);
        if (_fboTex != 0) GL.DeleteTexture(_fboTex);
        if (_depthRbo != 0) GL.DeleteRenderbuffer(_depthRbo);
        _fbo = 0; _fboTex = 0; _depthRbo = 0;
        if (_outFbo != 0) GL.DeleteFramebuffer(_outFbo);
        if (_outTex != 0) GL.DeleteTexture(_outTex);
        _outFbo = 0; _outTex = 0;
        if (_hbm != IntPtr.Zero) DeleteObject(_hbm);
        if (_hdcMem != IntPtr.Zero) DeleteDC(_hdcMem);
        _hbm = IntPtr.Zero; _hdcMem = IntPtr.Zero; _dibBits = IntPtr.Zero;
    }

    /// <summary>每帧:上下文切换 → 尺寸自愈/自愈重建 → 内容步进与绘制 → 降采样 → 读回 → ULW 上屏。</summary>
    public void RenderFrame()
    {
        if (!_initialized || _glc.IsDisposed) return;

        var time = (float)_clock.Elapsed.TotalSeconds;
        var delta = Math.Clamp(time - _lastTime, 0.001f, 0.1f);
        _lastTime = time;

        int w = _glc.ClientSize.Width, h = _glc.ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        try
        {
            // 关键(多桌宠):多个 OverlayWindow 共用同一 UI 线程,此刻 current context 很可能是
            // 另一个桌宠窗口。CreateOffscreen 里的资源创建必须在本窗口上下文下执行——
            // 否则 FBO 名称在自身上下文里无效,表现为画面变黑/花屏/尺寸互串。
            _glc.MakeCurrent();

            if (w != _dibW || h != _dibH)
                CreateOffscreen(w, h);

            // 自愈:本窗 FBO 因其它桌宠窗口销毁上下文而失效时立即重建(节流记录)
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            {
                if ((DateTime.Now - _lastRenderLog).TotalMilliseconds > 500)
                {
                    _lastRenderLog = DateTime.Now;
                    Log($"FBO incomplete {w}x{h}, recreating");
                }
                CreateOffscreen(w, h);
            }

            Content?.Update(delta);

            // 1) 按 SSAA 分辨率渲染(引擎自调 BeginFrame/相机/Viewport)
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            Content?.Render(_renderW, _renderH, _ssaa);

            // 2) 降采样到显示尺寸(bilinear 平均),消除边缘锯齿
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _outFbo);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.BlitFramebuffer(0, 0, _renderW, _renderH, 0, 0, w, h,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);

            // 3) 预乘 alpha BGRA 读入 DIB(显示尺寸,bottom-up 行序一致)
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outFbo);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Bgra, PixelType.UnsignedByte, _dibBits);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // 交给系统:逐像素合成 + 逐像素命中(透明处自动穿透)
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            var dst = new POINT(Left, Top);
            var size = new SIZE(w, h);
            var src = new POINT(0, 0);
            bool ulwOk = UpdateLayeredWindow(Handle, _screenDc, ref dst, ref size, _hdcMem, ref src, 0, ref blend, ULW_ALPHA);

            // 垂直同步屏障(可选):ULW 上屏本身不经历交换,SyncInterval=1 的 SwapBuffers
            // 会阻塞到下一个垂直消隐,把帧率钉在显示器刷新率
            if (_options.UseVSync)
                _glc.SwapBuffers();

            var n = Interlocked.Increment(ref _frameCount);
            if (n <= 5)
                Log($"frame#{n} pos=({Left},{Top}) size={w}x{h} ulw={ulwOk}");
            else if (!ulwOk && (DateTime.Now - _lastRenderLog).TotalMilliseconds > 500)
            {
                _lastRenderLog = DateTime.Now;
                Log($"ULW FAILED pos=({Left},{Top}) size={w}x{h}");
            }
        }
        catch (Exception ex)
        {
            // 窗口正在销毁/句柄失效时忽略一帧;真实异常节流记录(多桌宠排查)
            if ((DateTime.Now - _lastRenderLog).TotalMilliseconds > 500)
            {
                _lastRenderLog = DateTime.Now;
                Log($"RenderFrame EX: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ───────── 鼠标:左键拖动/点击判定,滚轮与中键轨道交给内容 ─────────

    private void OnViewMouseDown(object? sender, MouseEventArgs e)
    {
        var handled = Content?.MouseDown(e, _glc.ClientSize.Width, _glc.ClientSize.Height) == true;
        if (e.Button == MouseButtons.Left && !handled)
        {
            _dragging = true;
            _dragStartScreen = MousePosition;
            _downScreen = MousePosition;
            _downLocation = Location;
        }
        else if (e.Button == MouseButtons.Middle && _options.MiddleDragOrbit)
        {
            _orbiting = true;
            _orbitStartScreen = MousePosition;
            _orbitDownScreen = MousePosition;
        }
    }

    private void OnViewMouseUp(object? sender, MouseEventArgs e)
    {
        var handled = Content?.MouseUp(e, _glc.ClientSize.Width, _glc.ClientSize.Height) == true;

        if (_dragging && e.Button == MouseButtons.Left)
        {
            // 位移小于系统拖拽阈值视为点击,而不是拖动
            int dx = MousePosition.X - _downScreen.X, dy = MousePosition.Y - _downScreen.Y;
            var dragSize = SystemInformation.DragSize;
            if (!handled && Math.Abs(dx) < dragSize.Width && Math.Abs(dy) < dragSize.Height)
                ModelClick?.Invoke(ButtonName(e.Button));
            var moved = Location != _downLocation;
            _dragging = false;
            if (moved)
                PositionMoved?.Invoke(Location.X, Location.Y);
        }

        if (_orbiting && e.Button == MouseButtons.Middle)
        {
            int dx = MousePosition.X - _orbitDownScreen.X, dy = MousePosition.Y - _orbitDownScreen.Y;
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(dx) < dragSize.Width && Math.Abs(dy) < dragSize.Height)
                ModelClick?.Invoke("middle");
            _orbiting = false;
        }
    }

    private void OnViewMouseMove(object? sender, MouseEventArgs e)
    {
        ModelMouseMove?.Invoke(e);
        if (_dragging)
        {
            var d = MousePosition;
            Location = new System.Drawing.Point(Location.X + d.X - _dragStartScreen.X, Location.Y + d.Y - _dragStartScreen.Y);
            _dragStartScreen = d;
        }
        if (_orbiting)
        {
            var d = MousePosition;
            Content?.MiddleDrag(d.X - _orbitStartScreen.X, d.Y - _orbitStartScreen.Y);
        }
    }

    private void OnViewMouseWheel(object? sender, MouseEventArgs e)
        => Content?.Wheel(e.Delta > 0 ? 1.1f : 1f / 1.1f);

    private static string ButtonName(MouseButtons button) => button switch
    {
        MouseButtons.Right => "right",
        MouseButtons.Middle => "middle",
        _ => "left",
    };

    // ───────── 窗口 API ─────────

    /// <summary>按内容计算的尺寸调整窗口(FitToModel:内容按各自包围盒口径与工作区钳制返回客户区大小)。</summary>
    public void FitToModel()
    {
        var size = Content?.ComputeWindowSize(ScaleFactor);
        if (size is { } s)
            ClientSize = s;
    }

    /// <summary>在窗口 GL 上下文可用时执行动作(纹理上传等必须持上下文的操作;未建句柄时为空操作)。</summary>
    public void RunOnGL(Action action)
    {
        if (_glc is not { IsDisposed: false } glc || !glc.IsHandleCreated)
            return;
        glc.MakeCurrent();
        action();
    }

    /// <summary>完全鼠标穿透:开启后整个窗口所有输入落到下层窗口(此时无法拖动);
    /// 关闭后由 ULW 逐像素命中自动处理——透明处穿透、角色处可交互。</summary>
    public void SetClickThrough(bool enabled)
    {
        int style = GetWindowLong(Handle, GWL_EXSTYLE);
        if (enabled)
            SetWindowLong(Handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
        else
            SetWindowLong(Handle, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
    }

    /// <summary>在本窗口 GL 上下文仍存活时执行 GPU 资源清理(如释放模型纹理),须在 Dispose 之前调用。
    /// 多桌宠共用同一 UI 线程,若不在本窗上下文下删除纹理,删除会作用到其它桌宠的当前上下文。</summary>
    public void DisposeGpuResources(Action cleanup)
    {
        if (!_initialized || _glc.IsDisposed) return;
        try { _glc.MakeCurrent(); cleanup(); } catch { /* 清理失败不阻断窗口关闭 */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Log($"OnFormClosing reason={e.CloseReason}");
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
        _timer.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        Log("Dispose");
        if (disposing)
        {
            _timer.Stop();
            if (_initialized)
            {
                _glc?.MakeCurrent();
                ReleaseOffscreen();
                Content?.Dispose();
            }
            _glc?.Dispose();
            if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
        }
        base.Dispose(disposing);
    }
}

/// <summary>OverlayWindow 的引擎差异选项。</summary>
public sealed class OverlayOptions
{
    /// <summary>渲染定时器步长(毫秒):Spine/Live2D 15;VRM 8(帧率另由垂直同步钉扎)。</summary>
    public int FrameMs { get; init; } = 15;
    /// <summary>3D 引擎需要深度缓冲(离屏 FBO 挂深度渲染缓冲)。</summary>
    public bool DepthBuffer { get; init; }
    /// <summary>开启垂直同步(wglSwapInterval + 每帧 SwapBuffers 屏障,帧率钉在显示器刷新率)。</summary>
    public bool UseVSync { get; init; }
    /// <summary>中键按住拖动 = 轨道相机(VRM);未开启时中键事件交给内容。</summary>
    public bool MiddleDragOrbit { get; init; }
    /// <summary>窗口是否在任务栏显示(桌宠形态默认 false)。</summary>
    public bool ShowInTaskbar { get; init; }
    /// <summary>SSAA 超采样倍率:先按该倍数渲染再降采样,消除边缘锯齿。</summary>
    public float Supersample { get; init; } = 2f;
    /// <summary>诊断日志文件名(%LOCALAPPDATA%\APet\ 下);null = 关闭。</summary>
    public string? DiagLogName { get; init; }
    /// <summary>初始标题(适配器加载后经 SetWindowTitle 覆盖)。</summary>
    public string Title { get; init; } = "APet 显示窗口";
    /// <summary>初始客户区尺寸(加载模型前窗口处于隐藏态,实际尺寸由 FitToModel 决定)。</summary>
    public System.Drawing.Size InitialSize { get; init; } = new(700, 700);
}

/// <summary>
/// OverlayWindow 承载的渲染内容:引擎提供步进、绘制与自身视图状态;窗口负责壳与管线。
/// 鼠标方法返回 true 表示内容已消费该输入(窗口不执行默认拖动/点击判定)。
/// </summary>
public interface IOverlayContent : IDisposable
{
    /// <summary>OnLoad 时 GL 上下文已 MakeCurrent:初始化渲染器与静态资源。</summary>
    void OnGLReady();
    /// <summary>帧步进(秒,已做钳制)。</summary>
    void Update(double delta);
    /// <summary>绘制当前帧到已绑定的 SSAA 离屏 FBO(引擎自调 BeginFrame/相机/Viewport);width/height 为 SSAA 分辨率。</summary>
    void Render(int width, int height, float supersample);
    /// <summary>FitToModel:按缩放因子返回窗口客户区尺寸(引擎各自包围盒口径,内含工作区钳制与视图状态副作用)。</summary>
    System.Drawing.Size ComputeWindowSize(float scaleFactor);
    /// <summary>按下(e.X/e.Y 为客户区像素;w/h 为客户区尺寸,供反算模型坐标)。</summary>
    bool MouseDown(MouseEventArgs e, int width, int height);
    /// <summary>释放。</summary>
    bool MouseUp(MouseEventArgs e, int width, int height);
    /// <summary>移动。</summary>
    void MouseMove(MouseEventArgs e, int width, int height);
    /// <summary>滚轮缩放(乘算因子;未启用缩放的引擎可空实现)。</summary>
    void Wheel(float zoomFactor);
    /// <summary>中键轨道拖动(屏幕像素增量;仅 OverlayOptions.MiddleDragOrbit 时被调用)。</summary>
    void MiddleDrag(int dx, int dy);
}
