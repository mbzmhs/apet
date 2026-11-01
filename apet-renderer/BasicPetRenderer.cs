using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace APet.Renderer;

/// <summary>
/// 基础引擎渲染器:模型 = 图片文件夹,每个 png/gif 文件是一个动作(动作为文件名,含扩展名)。
/// - Load 只做轻量探测(png 读 IHDR 头、gif 解析帧延迟),不解码像素;首次播放某动作时才解码,
///   缓存超过上限后最久未用的被丢弃(再次播放会重新解码),避免整套立绘常驻内存;
/// - png 静态显示(无时长);gif 逐帧动画(按帧延迟推进,展示总时长);
/// - 非循环动作的完成时机:gif 一轮播完触发 MotionCompleted;png 显示约 1.2 秒后触发;
/// - 无眨眼/部件/表情概念,IPetRenderer 对应的默认接口方法均为空操作;
/// - 窗体为 WPF 透明悬浮窗(BasicPetWindow),在 WPF UI 线程创建,多实例互不干扰。
/// </summary>
public sealed class BasicPetRenderer : IPetRenderer
{
    private const int HoldFallbackMs = 30000; // 非循环动作的兜底完成:情绪图保持显示,正常由"TTS 朗读结束"切回待机,
                                              // 仅当朗读机制缺席(如 TTS 禁用)时超时回待机
    private const int MaxCachedMotions = 8;   // 已解码动作的缓存上限(LRU,不含当前播放)

    private sealed class GifAnim
    {
        public required BitmapSource[] Frames;
        public required int[] Delays;
    }

    private sealed class Motion
    {
        public required string Name;      // 文件名(含扩展名)
        public required string Path;
        public required bool IsGif;
        public required int PixelW;       // png 来自 IHDR;gif 为逻辑屏幕尺寸(首播后按实际帧校正)
        public required int PixelH;
        public double GifTotalSeconds;    // gif 轻量解析的帧延迟合计(列表展示,无需解码)
        public DateTime LastUsed;         // 缓存淘汰依据

        // 懒解码缓存:未解码为 null
        public BitmapImage? Png;
        public GifAnim? Gif;
    }

    private readonly Dictionary<string, Motion> _motions = new(StringComparer.OrdinalIgnoreCase);
    private string[] _sortedNames = [];
    private BasicPetWindow? _view;
    private DispatcherTimer? _frameTimer;
    private DispatcherTimer? _finishTimer;
    private GifAnim? _playingGif;
    private bool _fadeEnabled;
    private int _fadeMs = 250;
    private float _scale = 1f;
    private int _posX = 450;
    private int _posY = 100;
    private string? _current;
    private bool _currentLoop;
    private int _frameIndex;
    private bool _playing;
    private bool _loaded;

    public string Engine => "basic";
    public string EngineDisplayName => "基础";

    public bool IsLoaded => _loaded;
    public string? LoadError { get; private set; }

    public event Action<string>? ModelClicked;
    public event Action<string>? MotionCompleted;
    public event Action<int, int>? PositionMoved;

    public BasicPetRenderer()
    {
        _frameTimer = new DispatcherTimer(DispatcherPriority.Background);
        _frameTimer.Tick += OnFrameTick;
        _finishTimer = new DispatcherTimer(DispatcherPriority.Background);
        _finishTimer.Tick += (_, _) => Complete();
    }

    public string? Load(string modelPath, string? atlasPath)
    {
        if (!Directory.Exists(modelPath))
        {
            LoadError = "找不到模型文件夹: " + modelPath;
            return LoadError;
        }
        try
        {
            _motions.Clear();
            var files = Directory.EnumerateFiles(modelPath)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".png" or ".gif";
                })
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    var probe = ProbeGif(file);
                    if (probe is null)
                        continue;
                    _motions[name] = new Motion
                    {
                        Name = name,
                        Path = file,
                        IsGif = true,
                        PixelW = probe.Value.W,
                        PixelH = probe.Value.H,
                        GifTotalSeconds = probe.Value.TotalSeconds,
                    };
                }
                else
                {
                    var size = ProbePng(file);
                    if (size is null)
                        continue;
                    _motions[name] = new Motion
                    {
                        Name = name,
                        Path = file,
                        IsGif = false,
                        PixelW = size.Value.W,
                        PixelH = size.Value.H,
                    };
                }
            }

            if (_motions.Count == 0)
            {
                LoadError = "文件夹中没有可用的 png/gif 图片: " + modelPath;
                return LoadError;
            }
            _sortedNames = _motions.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            _loaded = true;
            LoadError = null;
            EnsureView();
            return null;
        }
        catch (Exception ex)
        {
            LoadError = "基础引擎模型加载失败: " + ex.Message;
            _loaded = false;
            HideView();
            return LoadError;
        }
    }

    // ───────── 轻量探测:只读文件头/帧延迟,不解码像素 ─────────

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static (int W, int H)? ProbePng(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (fs.Read(header) < 24 || !header[..8].SequenceEqual(PngSignature))
                return null;
            // IHDR:宽高为大端 4 字节(固定在第 16..23 字节)
            int w = header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19];
            int h = header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23];
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch
        {
            return null;
        }
    }

    private static (int W, int H, double TotalSeconds)? ProbeGif(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 14 || data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F')
                return null;
            int w = data[6] | data[7] << 8;
            int h = data[8] | data[9] << 8;
            var packed = data[10];
            var pos = 13;
            if ((packed & 0x80) != 0)
                pos += 3 << ((packed & 0x07) + 1); // 跳过全局颜色表
            double totalMs = 0;
            while (pos < data.Length)
            {
                var block = data[pos];
                if (block == 0x3B)
                    break; // 终止符
                if (block == 0x21)
                {
                    // 扩展块:图形控制扩展(0xF9)携带帧延迟(单位 10ms)
                    var label = data[pos + 1];
                    var sub = pos + 2;
                    if (label == 0xF9 && sub < data.Length && data[sub] == 4)
                    {
                        var delay = data[sub + 2] | data[sub + 3] << 8;
                        totalMs += delay > 0 ? delay * 10 : 100; // 延迟缺失按 100ms
                        sub += 4;
                    }
                    while (sub < data.Length && data[sub] != 0)
                        sub += 1 + data[sub];
                    pos = sub + 1;
                }
                else if (block == 0x2C)
                {
                    // 图像描述符:跳过颜色表与 LZW 数据子块
                    var localPacked = data[pos + 9];
                    var sub = pos + 10;
                    if ((localPacked & 0x80) != 0)
                        sub += 3 << ((localPacked & 0x07) + 1);
                    sub++; // LZW 最小码长字节
                    while (sub < data.Length && data[sub] != 0)
                        sub += 1 + data[sub];
                    pos = sub + 1;
                }
                else
                {
                    pos++; // 未知字节,防御性跳过
                }
            }
            return w > 0 && h > 0 ? (w, h, totalMs / 1000) : null;
        }
        catch
        {
            return null;
        }
    }

    // ───────── 懒解码缓存 ─────────

    private static BitmapImage? LoadPng(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // 单个损坏文件跳过,不影响其余动作
        }
    }

    private static GifAnim? LoadGif(string path)
    {
        try
        {
            var decoder = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            var frames = new BitmapSource[decoder.Frames.Count];
            var delays = new int[frames.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                var frame = decoder.Frames[i];
                frame.Freeze();
                frames[i] = frame;
                var delayMs = 100;
                if (frame.Metadata is BitmapMetadata meta)
                {
                    try
                    {
                        var raw = meta.GetQuery("/grctlext/Delay"); // 单位 10ms
                        if (raw is not null)
                            delayMs = Math.Max(10, Convert.ToInt32(raw) * 10);
                    }
                    catch (InvalidOperationException)
                    {
                        // 无图形控制扩展帧:沿用默认 100ms
                    }
                }
                delays[i] = delayMs;
            }
            return new GifAnim { Frames = frames, Delays = delays };
        }
        catch
        {
            return null;
        }
    }

    private Motion EnsureDecoded(Motion motion)
    {
        motion.LastUsed = DateTime.UtcNow;
        if (motion.IsGif ? motion.Gif is not null : motion.Png is not null)
            return motion;
        if (motion.IsGif)
            motion.Gif = LoadGif(motion.Path);
        else
            motion.Png = LoadPng(motion.Path);
        TrimCache();
        return motion;
    }

    /// <summary>缓存超限时丢弃最久未用的解码结果(当前播放的保留;再次播放会重新解码)。</summary>
    private void TrimCache()
    {
        var cached = _motions.Values
            .Where(m => m.IsGif ? m.Gif is not null : m.Png is not null)
            .ToList();
        if (cached.Count <= MaxCachedMotions)
            return;
        foreach (var m in cached
            .Where(m => !string.Equals(m.Name, _current, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.LastUsed)
            .Take(cached.Count - MaxCachedMotions))
        {
            m.Gif = null;
            m.Png = null;
        }
    }

    public void Unload()
    {
        StopTimers();
        _playing = false;
        _playingGif = null;
        _current = null;
        _motions.Clear();
        _sortedNames = [];
        _loaded = false;
        LoadError = null;
        HideView();
    }

    public string[] GetMotionNames() => (string[])_sortedNames.Clone();

    public PetMotionInfo[] GetMotionInfos() =>
        _sortedNames.Select(name =>
        {
            // png 无时长只显示文件名;gif 追加总时长(与 Spine 的 (x.xxs) 风格一致)
            var info = _motions[name];
            var detail = info.IsGif ? $"{name}  ({info.GifTotalSeconds:F2}s)" : name;
            return new PetMotionInfo { Name = name, Detail = detail };
        }).ToArray();

    public void Play(string motionName, bool loop)
    {
        if (!_loaded || !_motions.TryGetValue(motionName, out var motion) || _view is null)
            return;
        EnsureDecoded(motion);
        StopTimers();
        _current = motion.Name;
        _currentLoop = loop;
        _playing = true;
        _frameIndex = 0;

        BitmapSource first;
        int w, h;
        if (motion.IsGif)
        {
            var gif = motion.Gif;
            if (gif is null || gif.Frames.Length == 0)
                return; // 解码失败:保持当前画面
            _playingGif = gif;
            first = gif.Frames[0];
            w = first.PixelWidth;   // 实际帧尺寸校正(逻辑屏幕可能与帧不一致)
            h = first.PixelHeight;
        }
        else
        {
            var png = motion.Png;
            if (png is null)
                return;
            _playingGif = null;
            first = png;
            w = png.PixelWidth;
            h = png.PixelHeight;
        }
        motion.PixelW = w;
        motion.PixelH = h;
        _view.ShowFrame(first, w, h, _scale, _fadeEnabled, _fadeMs);

        if (_playingGif is { Frames.Length: > 1 })
        {
            _frameTimer.Interval = TimeSpan.FromMilliseconds(_playingGif.Delays[0]);
            _frameTimer.Start();
        }
        // 非循环动作(png/单帧 gif/多帧 gif 播完一轮)保持显示:回待机由"朗读结束"驱动
        // (宿主经插件 OnTtsIdle 切随机待机),仅在朗读机制缺席时走兜底超时
        if (!loop)
        {
            _finishTimer.Interval = TimeSpan.FromMilliseconds(HoldFallbackMs);
            _finishTimer.Start();
        }
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_playingGif is null || _view is null)
            return;
        _frameIndex++;
        if (_frameIndex >= _playingGif.Frames.Length)
        {
            if (!_currentLoop)
            {
                _frameTimer.Stop(); // 非循环 gif 播完一轮停在最后一帧,保持该情绪
                return;
            }
            _frameIndex = 0;
        }
        _view.UpdateFrame(_playingGif.Frames[_frameIndex]);
        _frameTimer!.Interval = TimeSpan.FromMilliseconds(_playingGif.Delays[_frameIndex]);
    }

    private void Complete()
    {
        StopTimers();
        _playing = false;
        _playingGif = null;
        if (_current is not null)
            MotionCompleted?.Invoke(_current);
    }

    private void StopTimers()
    {
        _frameTimer?.Stop();
        _finishTimer?.Stop();
    }

    public void SetScale(float scale)
    {
        if (scale <= 0f)
            return;
        _scale = scale;
        if (_current is not null && _motions.TryGetValue(_current, out var motion))
            _view?.Resize(motion.PixelW, motion.PixelH, _scale);
    }

    public void SetTransition(bool enabled, int durationMs)
    {
        _fadeEnabled = enabled;
        _fadeMs = durationMs is > 0 ? durationMs : 250;
    }

    private string? _windowTitle;
    public void SetWindowTitle(string title)
    {
        _windowTitle = title;
        if (_view is not null)
            _view.Title = title;
    }

    public void SetPosition(int x, int y)
    {
        // 落点不在任何屏幕上时回退默认位置,避免宠物"消失"在屏幕外找不回
        if (!IsOnAnyScreen(x, y))
        {
            x = 450;
            y = 100;
        }
        _posX = x;
        _posY = y;
        _view?.SetLocationPhysical(x, y);
    }

    public System.Drawing.Rectangle GetWindowBounds() => _view?.PhysicalBounds ?? System.Drawing.Rectangle.Empty;

    private static bool IsOnAnyScreen(int x, int y)
    {
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            if (x >= s.Bounds.Left && x < s.Bounds.Right && y >= s.Bounds.Top && y < s.Bounds.Bottom)
                return true;
        return false;
    }

    public string? CurrentMotion => _current;

    /// <summary>是否仍有动作在播:循环待机持续为 true;非循环动作完成后为 false。</summary>
    public bool IsMotionPlaying => _loaded && _playing;

    private void EnsureView()
    {
        if (_view is not null)
        {
            _view.Show();
            return;
        }
        _view = new BasicPetWindow();
        if (_windowTitle is not null)
            _view.Title = _windowTitle;
        _view.Click += button => ModelClicked?.Invoke(button);
        _view.PositionMoved += (x, y) => PositionMoved?.Invoke(x, y);
        _view.Show();                      // 先显示(此时无内容、0 尺寸透明,不可见)再落位,
        _view.SetLocationPhysical(_posX, _posY); // DPI 变换需要窗体已连接呈现源
    }

    private void HideView() => _view?.Hide();

    public void Dispose()
    {
        StopTimers();
        Unload();
        _view?.Close();
        _view = null;
    }
}
