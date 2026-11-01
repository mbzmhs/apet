using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace APet.Renderer.Live2D;

/// <summary>
/// 基于 OpenGL 3.3 的 Live2D 模型渲染器(配合 Cubism Core 的 Drawable 数据)。
/// 输出预乘 alpha 颜色,配合 DWM 逐像素透明窗口使用(清屏为全透明)。
/// 遮罩:每遇到一组新遮罩,把遮罩 Drawable 画进离屏 FBO,
/// 随后画目标 Drawable 时按 gl_FragCoord 采样遮罩(与官方高分屏遮罩方案等价的简化实现)。
/// </summary>
public unsafe class Live2DGLRenderer : IDisposable
{
    const string VertexShader = @"#version 330 core
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_uv;
uniform mat4 u_mvp;
out vec2 v_uv;
void main()
{
    v_uv = a_uv;
    gl_Position = u_mvp * vec4(a_pos, 0.0, 1.0);
}";

    // 注意:着色器源码内不能包含非 ASCII 字符(部分 GLSL 编译器会解析失败)
    const string FragmentShader = @"#version 330 core
in vec2 v_uv;
uniform sampler2D u_tex;
uniform sampler2D u_maskTex;
uniform float u_useMask;
uniform float u_invertMask;
uniform float u_alpha;
uniform vec3 u_mulColor;
uniform vec3 u_screenColor;
uniform vec2 u_viewport;
out vec4 o;
void main()
{
    vec4 c = texture(u_tex, v_uv);
    c.rgb *= u_mulColor;
    c.rgb = c.rgb + u_screenColor - c.rgb * u_screenColor;  // screen color
    float a = c.a * u_alpha;
    if (u_useMask > 0.5)
    {
        float m = texture(u_maskTex, gl_FragCoord.xy / u_viewport).a;
        float inside = smoothstep(0.4, 0.6, m);
        a *= mix(inside, 1.0 - inside, u_invertMask);
    }
    o = vec4(c.rgb * a, a);   // premultiplied alpha
}";

    const int StrideFloats = 4; // x, y, u, v

    int _program, _vbo, _vao;
    int _locMvp, _locUseMask, _locInvertMask, _locAlpha, _locMul, _locScreen, _locViewport;
    int _maskFbo, _maskTex;
    int _maskW, _maskH;
    int _viewportW, _viewportH;
    bool _initialized;

    float[] _verts = new float[1024 * StrideFloats];
    readonly List<int> _textures = new();
    readonly Dictionary<int, int> _eboCache = new();   // drawable 下标 → 索引缓冲

    public void Init()
    {
        if (_initialized) return;

        int vs = Compile(ShaderType.VertexShader, VertexShader);
        int fs = Compile(ShaderType.FragmentShader, FragmentShader);
        _program = GL.CreateProgram();
        GL.AttachShader(_program, vs);
        GL.AttachShader(_program, fs);
        GL.LinkProgram(_program);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new ApplicationException("着色器链接失败: " + GL.GetProgramInfoLog(_program));
        GL.DetachShader(_program, vs);
        GL.DetachShader(_program, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        _locMvp = GL.GetUniformLocation(_program, "u_mvp");
        _locUseMask = GL.GetUniformLocation(_program, "u_useMask");
        _locInvertMask = GL.GetUniformLocation(_program, "u_invertMask");
        _locAlpha = GL.GetUniformLocation(_program, "u_alpha");
        _locMul = GL.GetUniformLocation(_program, "u_mulColor");
        _locScreen = GL.GetUniformLocation(_program, "u_screenColor");
        _locViewport = GL.GetUniformLocation(_program, "u_viewport");

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int stride = StrideFloats * sizeof(float);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);
        GL.UseProgram(_program);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(GL.GetUniformLocation(_program, "u_tex"), 0);
        GL.Uniform1(GL.GetUniformLocation(_program, "u_maskTex"), 1);
        _initialized = true;
    }

    static int Compile(ShaderType type, string src)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, src);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new ApplicationException(type + " 编译失败: " + GL.GetShaderInfoLog(shader));
        return shader;
    }

    /// <summary>加载一张 PNG 纹理,返回纹理句柄(须在 GL 上下文中调用)。</summary>
    public int LoadTexture(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        int width = bmp.Width, height = bmp.Height;
        var data = new byte[width * height * 4];
        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var bits = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 首行 = 图像顶部上传(v=0 对应图像顶部);Core 的 UV v=0 在图集底部,加载模型时翻转 v
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride,
                    data, y * width * 4, width * 4);
        }
        finally { bmp.UnlockBits(bits); }

        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, data);
        _textures.Add(tex);
        return tex;
    }

    public void DisposeTextures()
    {
        foreach (var t in _textures) GL.DeleteTexture(t);
        _textures.Clear();
    }

    /// <summary>清屏并设置相机:zoom 为缩放,panX/panY 为模型坐标系下的视口中心。
    /// 模型坐标系 y 向上(与 Spine 一致),正交直接映射。</summary>
    public void BeginFrame(int width, int height, float zoom, float panX, float panY,
        float clearR = 0f, float clearG = 0f, float clearB = 0f, float clearA = 0f)
    {
        _viewportW = width; _viewportH = height;
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(clearR, clearG, clearB, clearA);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        float halfW = width / (2f * zoom), halfH = height / (2f * zoom);
        // 模型坐标系 y 向上,GL 裁剪空间 y 向上,直接正交映射
        var mvp = Matrix4.CreateOrthographicOffCenter(
            -halfW + panX, halfW + panX,
            -halfH + panY, halfH + panY,
            -1f, 1f);

        GL.UseProgram(_program);
        GL.UniformMatrix4(_locMvp, false, ref mvp);
        // 必须显式转 float:width/height 是 int,否则会命中 Uniform2(int,int,int) 整型重载
        // (glUniform2i 对 vec2 uniform 无效),u_viewport 将保持 (0,0),遮罩采样除零全灭
        GL.Uniform2(_locViewport, (float)width, (float)height);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(_vao);
    }

    /// <summary>诊断:绘制整个模型(按渲染顺序;须在 BeginFrame 之后)。</summary>
    public void DrawModel(Cubism.CubismModel model, float modelAlpha = 1f, bool flipX = false, bool flipY = false)
    {
        if (model?.Drawables == null) return;

        // 可见 Drawable 按渲染顺序排序
        var list = new List<Cubism.CubismDrawable>(model.Drawables.Length);
        foreach (var d in model.Drawables)
            if (d.Visible && d.Opacity > 0.001f) list.Add(d);
        list.Sort((a, b) => a.RenderOrder.CompareTo(b.RenderOrder));

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(_locUseMask, 0f);
        GL.Uniform1(_locInvertMask, 0f);

        string currentKey = null;
        foreach (var d in list)
        {
            string key = MaskKey(d);
            if (key != currentKey)
            {
                currentKey = key;
                if (key == null)
                {
                    GL.ActiveTexture(TextureUnit.Texture1);
                    GL.BindTexture(TextureTarget.Texture2D, 0);
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.Uniform1(_locUseMask, 0f);
                }
                else
                {
                    RenderMasks(model, d, flipX, flipY);
                    GL.Uniform1(_locUseMask, 1f);
                    GL.Uniform1(_locInvertMask, d.InvertedMask ? 1f : 0f);
                }
            }

            DrawDrawable(d, modelAlpha, flipX, flipY);
        }

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Uniform1(_locUseMask, 0f);
    }

    static string MaskKey(Cubism.CubismDrawable d)
    {
        if (d.Masks == null || d.Masks.Length == 0) return null;
        var sorted = d.Masks.OrderBy(m => m);
        return string.Join(",", sorted);
    }

    /// <summary>把目标 Drawable 的遮罩集合渲染到遮罩 FBO(与主通道使用同一相机与翻转)。</summary>
    void RenderMasks(Cubism.CubismModel model, Cubism.CubismDrawable target, bool flipX, bool flipY)
    {
        // 记住当前 FBO(通常是主离屏 FBO),遮罩渲染完后恢复
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int prevFbo);
        EnsureMaskBuffer(_viewportW, _viewportH);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        GL.Viewport(0, 0, _maskW, _maskH);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);   // 遮罩 alpha 叠加
        // 遮罩通道自身绝不能应用遮罩:上一组主通道已把 u_useMask 置 1,若不清零,
        // 画遮罩形状时会采样刚清空的 FBO(全透明)→ a*=0 → 本组遮罩全灭(第二只眼瞳孔丢失的根因)
        GL.Uniform1(_locUseMask, 0f);
        GL.Uniform1(_locInvertMask, 0f);

        foreach (var mi in target.Masks)
        {
            var mask = model.Drawables[mi];
            if (mask == null) continue;
            // Cubism 约定:遮罩形状即使自身不可见也要参与裁剪(官方渲染器画遮罩不检查可见性)
            DrawDrawable(mask, 1f, flipX, flipY, isMaskPass: true);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevFbo);
        GL.Viewport(0, 0, _viewportW, _viewportH);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);  // 恢复预乘 normal

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _maskTex);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    void EnsureMaskBuffer(int w, int h)
    {
        if (_maskFbo != 0 && _maskW == w && _maskH == h) return;
        ReleaseMaskBuffer();
        _maskW = w; _maskH = h;   // 必须更新,RenderMasks 的视口依赖它(否则 0x0 视口把遮罩全部裁掉)
        _maskTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _maskTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _maskFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _maskTex, 0);
        // 不改变 FBO 绑定状态:调用方负责绑定/恢复(RenderMasks 已保存之前的绑定)
    }

    void ReleaseMaskBuffer()
    {
        if (_maskFbo != 0) GL.DeleteFramebuffer(_maskFbo);
        if (_maskTex != 0) GL.DeleteTexture(_maskTex);
        _maskFbo = 0; _maskTex = 0;
    }

    void DrawDrawable(Cubism.CubismDrawable d, float alpha, bool flipX, bool flipY, bool isMaskPass = false)
    {
        if (d.TextureIndex < 0 || d.TextureIndex >= _textures.Count) return;
        int vc = d.VertexCount;
        int floats = vc * StrideFloats;
        if (_verts.Length < floats) _verts = new float[Math.Max(floats, _verts.Length * 2)];

        // 遮罩通道里遮罩 Drawable 即使自身不可见也要画(只作裁剪形状),
        // 且不做背面剔除——见 RenderMasks / 下方 CullFace 分支
        for (int v = 0; v < vc; v++)
        {
            float x = d.VertexPositions[v * 2];
            float y = d.VertexPositions[v * 2 + 1];
            _verts[v * 4] = flipX ? -x : x;
            _verts[v * 4 + 1] = flipY ? -y : y;
            _verts[v * 4 + 2] = d.VertexUvs[v * 2];
            _verts[v * 4 + 3] = d.VertexUvs[v * 2 + 1];
        }

        GL.BindTexture(TextureTarget.Texture2D, _textures[d.TextureIndex]);
        if (isMaskPass)
        {
            GL.Uniform1(_locAlpha, 1f);
            GL.Uniform3(_locMul, 1f, 1f, 1f);
            GL.Uniform3(_locScreen, 0f, 0f, 0f);
        }
        else
        {
            GL.Uniform1(_locAlpha, d.Opacity * alpha);
            GL.Uniform3(_locMul, d.MultiplyR, d.MultiplyG, d.MultiplyB);
            GL.Uniform3(_locScreen, d.ScreenR, d.ScreenG, d.ScreenB);
        }

        // 常量 flag:1<<0 additive,1<<1 multiplicative(与 Live2DCubismCore.h 一致)
        SetBlend(d.BlendMode);

        // 背面剔除:单面网格剔除背面(官方行为);双面网格保留两面。
        // 遮罩通道一律不剔除——裁剪形状的法线朝向不应影响遮罩覆盖范围
        if (isMaskPass || d.DoubleSided) GL.Disable(EnableCap.CullFace);
        else { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); }

        GL.BufferData(BufferTarget.ArrayBuffer, floats * sizeof(float), _verts, BufferUsageHint.StreamDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, GetOrCreateEbo(d));
        GL.DrawElements(PrimitiveType.Triangles, d.Indices.Length, DrawElementsType.UnsignedShort, 0);
    }

    int GetOrCreateEbo(Cubism.CubismDrawable d)
    {
        if (_eboCache.TryGetValue(d.Index, out int ebo)) return ebo;
        ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, d.Indices.Length * sizeof(ushort), d.Indices, BufferUsageHint.StaticDraw);
        _eboCache[d.Index] = ebo;
        return ebo;
    }

    /// <summary>模型卸载时清空索引缓冲缓存。</summary>
    public void ClearDrawables()
    {
        foreach (var ebo in _eboCache.Values) GL.DeleteBuffer(ebo);
        _eboCache.Clear();
    }

    void SetBlend(Live2DBlendMode mode)
    {
        switch (mode)
        {
            case Live2DBlendMode.Additive:
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                break;
            case Live2DBlendMode.Multiplicative:
                GL.BlendFuncSeparate(BlendingFactorSrc.DstColor, BlendingFactorDest.OneMinusSrcAlpha,
                    BlendingFactorSrc.Zero, BlendingFactorDest.One);
                break;
            default: // Normal:源为预乘 alpha
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    public void Dispose()
    {
        if (!_initialized) return;
        DisposeTextures();
        ClearDrawables();
        ReleaseMaskBuffer();
        GL.DeleteProgram(_program);
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        _initialized = false;
    }
}

/// <summary>Live2D Drawable 的混合模式。</summary>
public enum Live2DBlendMode
{
    Normal,
    Additive,
    Multiplicative,
}
