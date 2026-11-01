using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.WinForms;
using System.Windows.Forms;

namespace APet.Renderer.Vrm;

/// <summary>
/// VRM 的 OverlayWindow 渲染内容:GL 渲染器 + 模型延迟上传 + 轨道相机。
/// 每帧按模型包围盒自适应距离取景(透视相机在模型 -Z 侧),中键拖动环绕旋转。
/// </summary>
public sealed class VrmOverlayContent : IOverlayContent
{
    readonly VrmGLRenderer _renderer = new();
    private int _vao; // 必须在 OnGLReady(上下文就绪)创建:构造期无上下文,GenVertexArray 得到无效对象
    private VrmStage? _stage;
    private VrmStage? _pendingUpload; // 已设置但尚未在 GL 上下文中上传资源的模型

    // 环绕视角:默认视角在模型 -Z 侧(yaw=pitch=0);中键拖动改 yaw(绕 Y)与 pitch(俯仰)
    private float _orbitYaw, _orbitPitch;
    private const float OrbitSensitivity = 0.008f; // 弧度/像素

    /// <summary>已就绪(资源已上传)的舞台;SetStage 后在首个渲染帧完成上传。</summary>
    public VrmStage? Stage => _stage;

    public void OnGLReady()
    {
        _renderer.Init();
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
    }

    /// <summary>设置要显示的模型(资源在本窗 GL 上下文可用时自动上传)。</summary>
    public void SetStage(VrmStage stage) => _pendingUpload = stage;

    /// <summary>渲染风格:0 = toon 二次元,1 = pbr 写实,2 = mtoon 官方。</summary>
    public void SetShading(int mode) => _renderer.SetShading(mode);

    /// <summary>把环绕视角复位到默认正面(模型 -Z 侧)。</summary>
    public void ResetOrbit()
    {
        _orbitYaw = 0f;
        _orbitPitch = 0f;
    }

    public void Update(double delta) => _stage?.Update();

    public void Render(int width, int height, float supersample)
    {
        GL.BindVertexArray(_vao); // GLControl 内部会重绑自己的 VAO,每帧恢复我们的顶点数组状态

        if (_pendingUpload is { } pending)
        {
            _renderer.SetModel(pending.Model); // 模型资源上传必须在 GL 上下文中
            _stage = pending;
            _pendingUpload = null;
        }
        if (_stage is not { } stage)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            return;
        }

        // 取景:透视相机放在模型 -Z 侧(VRM 模型面向 -Z),按包围盒自适应距离。
        // 数学约定与 VrmStage 一致(列向量,平移第四列),投影/视图手写,不经 OpenTK 矩阵语义。
        var model = stage.Model;
        var center = model.Center;
        var size = model.Size;
        var fov = MathHelper.DegreesToRadians(30f);
        var dist = size.Y / 2f / MathF.Tan(fov / 2f) + 0.2f;
        var aspect = width / (float)Math.Max(1, height);
        var f = 1f / MathF.Tan(fov / 2f);
        // 标准透视(列向量):第三行放 z 变换系数,第四行 M43=-1
        var projection = new Matrix4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (dist * 4f + 0.02f) / (0.02f - dist * 4f), 2f * 0.02f * dist * 4f / (0.02f - dist * 4f),
            0, 0, -1, 0);
        // 数学列向量 view:三行 = 轴,平移在【第 4 列】(与 Compose/MatMul/XformPoint 同约定)
        // 环绕视角:视线方向由 yaw/pitch 参数化,yaw=pitch=0 即默认正面(eye 在 -Z 侧)
        var forward = new Vector3(
            MathF.Cos(_orbitPitch) * MathF.Sin(_orbitYaw),
            MathF.Sin(_orbitPitch),
            MathF.Cos(_orbitPitch) * MathF.Cos(_orbitYaw));
        var eye = center - forward * dist;
        var zAxis = -forward; // 相机 backward 方向
        var xAxis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, zAxis));
        var yAxis = Vector3.Cross(zAxis, xAxis);
        var view = new Matrix4(
            xAxis.X, xAxis.Y, xAxis.Z, -Vector3.Dot(xAxis, eye),
            yAxis.X, yAxis.Y, yAxis.Z, -Vector3.Dot(yAxis, eye),
            zAxis.X, zAxis.Y, zAxis.Z, -Vector3.Dot(zAxis, eye),
            0, 0, 0, 1);
        var vp = VrmStage.MatMul(projection, view);
        var light = Vector3.Normalize(new Vector3(-0.35f, 0.85f, -0.9f));

        GL.Viewport(0, 0, width, height);
        _renderer.RenderFrame(model, vp, light, eye);
    }

    // 鼠标:左键点击/拖动由窗口默认行为处理;中键轨道经 MiddleDrag 注入,内容不消费按下事件

    public bool MouseDown(MouseEventArgs e, int width, int height) => false;

    public bool MouseUp(MouseEventArgs e, int width, int height) => false;

    public void MouseMove(MouseEventArgs e, int width, int height) { }

    public void Wheel(float zoomFactor) { } // VRM 无滚轮缩放(取景按包围盒自适应)

    public void MiddleDrag(int dx, int dy)
    {
        _orbitYaw += dx * OrbitSensitivity;
        // 俯仰钳制在 ±85°,避免绕到正上/正下时 up 轴退化
        _orbitPitch = Math.Clamp(_orbitPitch + dy * OrbitSensitivity,
            -MathHelper.DegreesToRadians(85f), MathHelper.DegreesToRadians(85f));
    }

    /// <summary>按模型包围盒 × 缩放因子计算窗口客户区尺寸(米制:ScaleFactor=1 → 1000px/米)。
    /// 工作区钳制按同一口径换算,否则大模型窗口会超出屏幕(只看到局部)。</summary>
    public System.Drawing.Size ComputeWindowSize(float scaleFactor)
    {
        var model = (_pendingUpload ?? _stage)?.Model;
        if (model is null)
            return new System.Drawing.Size(600, 900);
        float bw = model.Size.X, bh = model.Size.Y;

        const float margin = 1.15f;
        const float pxPerUnit = 1000f;
        var wa = SystemInformation.WorkingArea;
        float fitScale = Math.Min(wa.Width * 0.9f / (bw * pxPerUnit * margin),
                                  wa.Height * 0.9f / (bh * pxPerUnit * margin));
        float scale = MathF.Max(0.01f, Math.Min(scaleFactor, fitScale));
        int w = Math.Max(8, (int)MathF.Ceiling(bw * scale * pxPerUnit * margin));
        int h = Math.Max(8, (int)MathF.Ceiling(bh * scale * pxPerUnit * margin));
        return new System.Drawing.Size(w, h);
    }

    public void Dispose() => _renderer.Dispose();
}
