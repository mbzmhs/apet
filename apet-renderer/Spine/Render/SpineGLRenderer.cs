using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using APet.Renderer.Spine.Runtime;

namespace APet.Renderer.Spine;

/// <summary>
/// 基于 OpenGL 3.3 的 Spine 骨架渲染器。
/// 输出预乘 alpha 颜色,配合 DWM 逐像素透明窗口使用(清屏为全透明)。
/// </summary>
public class SpineGLRenderer : IDisposable
{
    const string VertexShader = @"#version 330 core
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_uv;
layout(location = 2) in vec4 a_color;
uniform mat4 u_mvp;
out vec2 v_uv;
out vec4 v_color;
void main()
{
    v_uv = a_uv;
    v_color = a_color;
    gl_Position = u_mvp * vec4(a_pos, 0.0, 1.0);
}";

    // 注意:着色器源码内不能包含非 ASCII 字符(部分 GLSL 编译器会解析失败)
    const string FragmentShader = @"#version 330 core
in vec2 v_uv;
in vec4 v_color;
uniform sampler2D u_tex;
uniform float u_premult;
out vec4 o;
void main()
{
    vec4 c = texture(u_tex, v_uv) * v_color;
    o = vec4(mix(c.rgb * c.a, c.rgb, u_premult), c.a);
}";

    const int StrideFloats = 8; // x, y, u, v, r, g, b, a

    int _program, _vbo, _vao;
    int _locMvp, _locTint, _locPremult, _locTex;
    int _whiteTex;
    bool _initialized;

    float[] _world = new float[1024];
    float[] _verts = new float[1024 * StrideFloats];
    readonly int[] _quadTriangles = { 0, 1, 2, 2, 3, 0 };
    readonly SkeletonClipping _clipping = new();

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
        _locTint = GL.GetUniformLocation(_program, "u_tint");
        _locPremult = GL.GetUniformLocation(_program, "u_premult");
        _locTex = GL.GetUniformLocation(_program, "u_tex");

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int stride = StrideFloats * sizeof(float);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.EnableVertexAttribArray(2);
        GL.BindVertexArray(0);

        _whiteTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _whiteTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
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

    /// <summary>清屏并设置相机:zoom 为缩放,panX/panY 为骨架坐标系下的视口中心,rgba 为清屏色(透明窗口用 0,0,0,0)。</summary>
    public void BeginFrame(int width, int height, float zoom, float panX, float panY,
        float clearR = 0f, float clearG = 0f, float clearB = 0f, float clearA = 0f)
    {
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(clearR, clearG, clearB, clearA);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        float halfW = width / (2f * zoom), halfH = height / (2f * zoom);
        // 骨架坐标系 y 向上,GL 裁剪空间 y 也向上,直接正交映射
        var mvp = Matrix4.CreateOrthographicOffCenter(
            -halfW + panX, halfW + panX,
            -halfH + panY, halfH + panY,
            -1f, 1f);

        GL.UseProgram(_program);
        GL.UniformMatrix4(_locMvp, false, ref mvp);
        GL.Uniform1(_locTex, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(_vao);
    }

    public void DrawSkeleton(Skeleton skeleton)
    {
        var slots = skeleton.DrawOrder;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots.Items[i];
            var attachment = slot.Attachment;
            if (attachment == null) continue;

            if (attachment is ClippingAttachment clipAttach)
            {
                _clipping.ClipStart(slot, clipAttach);
                continue;
            }

            // 4.2 序列帧附件:按 slot.SequenceIndex 切换到正确的图集区域
            if (attachment is IHasTextureRegion hasTex && hasTex.Sequence != null)
                hasTex.Sequence.Apply(slot, hasTex);

            float[] world, uvs;
            int[] triangles;
            if (attachment is RegionAttachment region)
            {
                EnsureWorldCapacity(8);
                region.ComputeWorldVertices(slot, _world, 0);
                world = _world;
                uvs = region.UVs;
                triangles = _quadTriangles;
            }
            else if (attachment is MeshAttachment mesh)
            {
                int wvl = mesh.WorldVerticesLength;
                EnsureWorldCapacity(wvl);
                mesh.ComputeWorldVertices(slot, 0, wvl, _world, 0);
                world = _world;
                uvs = mesh.UVs;
                triangles = mesh.Triangles;
            }
            else
            {
                continue;
            }

            var atlasRegion = ((IHasTextureRegion)attachment).Region as AtlasRegion;
            if (atlasRegion?.page?.rendererObject is not int tex) continue;

            float[] drawVerts = world;
            float[] drawUvs = uvs;
            int[] drawTris = triangles;
            bool clippingActive = _clipping.IsClipping;
            if (clippingActive)
            {
                _clipping.ClipTriangles(world, triangles, triangles.Length, uvs);
                if (_clipping.ClippedTriangles.Count == 0)
                {
                    _clipping.ClipEnd(slot);
                    continue;
                }
                drawVerts = _clipping.ClippedVertices.Items;
                drawUvs = _clipping.ClippedUVs.Items;
                drawTris = _clipping.ClippedTriangles.Items;
            }

            // 4.2:skeleton(R/G/B/A) x slot(R/G/B/A) 为最终染色
            float r = skeleton.R * slot.R;
            float g = skeleton.G * slot.G;
            float b = skeleton.B * slot.B;
            float a = skeleton.A * slot.A;

            SetBlendMode(slot.Data.BlendMode);
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.Uniform4(_locTint, r, g, b, a);
            GL.Uniform1(_locPremult, atlasRegion.page.pma ? 1f : 0f);

            int vertCount = DrawTriangles(drawVerts, drawUvs, drawTris, r, g, b, a);
            if (vertCount > 0)
            {
                GL.BufferData(BufferTarget.ArrayBuffer, vertCount * StrideFloats * sizeof(float),
                    _verts, BufferUsageHint.StreamDraw);
                GL.DrawArrays(PrimitiveType.Triangles, 0, vertCount);
            }

            _clipping.ClipEnd(slot);
        }
        _clipping.ClipEnd();
    }

    int DrawTriangles(float[] verts, float[] uvs, int[] triangles, float r, float g, float b, float a)
    {
        int outFloats = 0;
        int needed = triangles.Length * 3 * StrideFloats;
        if (_verts.Length < needed) _verts = new float[Math.Max(needed, _verts.Length * 2)];
        for (int i = 0; i < triangles.Length; i++)
        {
            int vi = triangles[i] * 2;
            _verts[outFloats++] = verts[vi];
            _verts[outFloats++] = verts[vi + 1];
            _verts[outFloats++] = uvs[vi];
            _verts[outFloats++] = uvs[vi + 1];
            _verts[outFloats++] = r;
            _verts[outFloats++] = g;
            _verts[outFloats++] = b;
            _verts[outFloats++] = a;
        }
        return outFloats / StrideFloats;
    }

    public void DrawDebug(Skeleton skeleton, bool showBones, bool showBounds)
    {
        GL.BindTexture(TextureTarget.Texture2D, _whiteTex);
        GL.Uniform4(_locTint, 1f, 1f, 1f, 1f);
        GL.Uniform1(_locPremult, 0f);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        int boneFloats = 0;
        if (showBones)
            boneFloats = (skeleton.Bones.Count * 2 + 4) * 2; // 每根骨骼一条线段(4个数),根骨骼十字(8个数)
        int boundFloats = showBounds ? 8 * 2 : 0;
        if (boneFloats + boundFloats == 0) return;

        var lines = new float[boneFloats + boundFloats];
        int n = 0;
        if (showBones)
        {
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var bone = skeleton.Bones.Items[i];
                if (bone.Parent == null) continue;
                lines[n++] = bone.Parent.WorldX;
                lines[n++] = bone.Parent.WorldY;
                lines[n++] = bone.WorldX;
                lines[n++] = bone.WorldY;
            }
            var root = skeleton.RootBone;
            if (root != null)
            {
                float cx = root.WorldX, cy = root.WorldY, s = 10f;
                lines[n++] = cx - s; lines[n++] = cy; lines[n++] = cx + s; lines[n++] = cy;
                lines[n++] = cx; lines[n++] = cy - s; lines[n++] = cx; lines[n++] = cy + s;
            }
        }
        if (showBounds)
        {
            var d = skeleton.Data;
            float l = d.X, rgt = d.X + d.Width, top = d.Y + d.Height, bot = d.Y;
            lines[n++] = l; lines[n++] = bot; lines[n++] = rgt; lines[n++] = bot;
            lines[n++] = rgt; lines[n++] = bot; lines[n++] = rgt; lines[n++] = top;
            lines[n++] = rgt; lines[n++] = top; lines[n++] = l; lines[n++] = top;
            lines[n++] = l; lines[n++] = top; lines[n++] = l; lines[n++] = bot;
        }

        int floats = n * StrideFloats;
        if (_verts.Length < floats) _verts = new float[floats];
        int k = 0;
        for (int v = 0; v < n; v += 2)
        {
            bool isBoundLine = v >= boneFloats;
            _verts[k++] = lines[v];
            _verts[k++] = lines[v + 1];
            _verts[k++] = 0f; _verts[k++] = 0f;
            _verts[k++] = isBoundLine ? 0.2f : 1.0f;   // r
            _verts[k++] = isBoundLine ? 0.8f : 0.5f;   // g
            _verts[k++] = isBoundLine ? 0.4f : 0.2f;   // b
            _verts[k++] = 1f;
        }
        GL.BufferData(BufferTarget.ArrayBuffer, k * sizeof(float), _verts, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, k / StrideFloats);
    }

    void SetBlendMode(BlendMode mode)
    {
        switch (mode)
        {
            case BlendMode.Additive:
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                break;
            case BlendMode.Multiply:
                GL.BlendFuncSeparate(BlendingFactorSrc.DstColor, BlendingFactorDest.OneMinusSrcAlpha,
                    BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
                break;
            case BlendMode.Screen:
                GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcColor,
                    BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
                break;
            default: // Normal:源为预乘 alpha
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    void EnsureWorldCapacity(int floats)
    {
        if (_world.Length < floats) _world = new float[Math.Max(floats, _world.Length * 2)];
    }

    public void Dispose()
    {
        if (!_initialized) return;
        GL.DeleteProgram(_program);
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        GL.DeleteTexture(_whiteTex);
        _initialized = false;
    }
}
