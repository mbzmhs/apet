using APet.Renderer.Spine;
using APet.Renderer.Spine.Runtime;
using System.Windows.Forms;

namespace APet.Renderer;

/// <summary>
/// Spine 引擎渲染器:SpineStage(骨架/动画)+ OverlayWindow(逐像素透明的悬浮窗)。
/// 透明像素自动鼠标穿透,模型像素可点击/拖动;窗口置顶、不进任务栏。
/// </summary>
public sealed class SpinePetRenderer : PetRendererBase
{
    private readonly SpineOverlayContent _content = new();
    private readonly SpineStage _stage;
    private OverlayWindow? _view;

    public SpinePetRenderer()
    {
        _stage = _content.Stage;
        // 轨道 0 动画播完(循环动作每圈):上报给插件层做待机轮换/回待机
        _stage.OnTrackComplete += entry =>
        {
            if (entry.TrackIndex == 0 && entry.Animation?.Name is { } name)
                RaiseMotionCompleted(name);
        };
    }

    public override string Engine => "spine";
    public override string EngineDisplayName => "Spine";
    public override bool IsLoaded => _stage.IsLoaded;
    public override string? CurrentMotion => _viewShown || _stage.IsLoaded
        ? _stage.CurrentTrack?.Animation?.Name
        : null;

    /// <summary>是否仍有动作在播(循环待机每圈完成时轨道条目仍在,保持 true)。</summary>
    public override bool IsMotionPlaying => _stage.IsLoaded && _stage.CurrentTrack is not null;

    protected override string? LoadCore(string modelPath, string? atlasPath)
    {
        var atlas = atlasPath;
        if (string.IsNullOrWhiteSpace(atlas))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            var baseName = Path.GetFileNameWithoutExtension(modelPath);
            atlas = new[] { baseName + ".atlas", baseName + ".atlas.txt" }
                .Select(name => Path.Combine(dir ?? ".", name))
                .FirstOrDefault(File.Exists)
                ?? Directory.GetFiles(dir ?? ".", "*.atlas").FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(atlas) || !File.Exists(atlas))
            return "未找到 .atlas 图集文件,请在设置里手动指定。";

        // 必须先建窗:OverlayWindow.OnLoad 会 MakeCurrent 建立 GL 上下文,
        // 而 _stage.Load 内部上传纹理(GL.GenTexture/TexImage2D)依赖当前线程已有上下文,
        // 顺序颠倒会在 GenTexture 处抛"no current context"。窗口在首帧渲染前是全透明的,提前 Show 无副作用。
        EnsureView();
        _stage.Load(modelPath, atlas);
        OnScaleApplied(); // 窗口尺寸与显示比例由缩放因子决定(包围盒 × ScaleFactor),而非固定适配
        return null;
    }

    /// <summary>创建悬浮窗(置顶、不进任务栏);首次成功加载模型时显示。</summary>
    private void EnsureView()
    {
        if (_view is not null)
            return;
        _view = new OverlayWindow(new OverlayOptions
        {
            DiagLogName = "spine-overlay-diag.log",
        })
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
        _stage.Dispose();
        LoadError = null;
    }

    public override string[] GetMotionNames()
    {
        var names = _stage.GetAnimationNames();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public override PetMotionInfo[] GetMotionInfos()
    {
        var names = _stage.GetAnimationNames();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names.Select(name =>
        {
            var info = _stage.GetAnimationInfo(name);
            // 与 SpinePlayer 一致:基础名 + 该动作切换的附件(语义标签)+ 时长
            var hint = info?.AttachmentNames is { Length: > 0 } a ? string.Join(",", a) : null;
            var detail = hint is null
                ? $"{name}   ({info?.Duration:F2}s)"
                : $"{name}  {hint}  ({info?.Duration:F2}s)";
            return new PetMotionInfo { Name = name, Detail = detail };
        }).ToArray();
    }

    public override void Play(string motionName, bool loop)
    {
        if (!_stage.IsLoaded)
            return;
        _stage.Play(motionName, loop, mix: 0.2f, trackIndex: 0);
    }

    protected override void OnScaleApplied()
    {
        if (_view is not null)
        {
            _view.ScaleFactor = _scale;
            if (_stage.IsLoaded)
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
        if (_view is not null)
        {
            // 先在本窗口上下文仍存活时释放模型纹理(DeleteTexture 依赖当前上下文),
            // 多桌宠下否则会作用到其它桌宠的当前上下文;再销毁窗口与上下文。
            _view.DisposeGpuResources(() => _stage.Dispose());
            // OnFormClosing 会把用户关闭转为 Hide,Dispose 走真正关闭
            _view.Close();
            _view.Dispose();
            _view = null;
            _viewShown = false;
        }
        else
        {
            _stage.Dispose();
        }
    }
}
