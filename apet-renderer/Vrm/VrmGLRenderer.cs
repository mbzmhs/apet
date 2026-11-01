using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace APet.Renderer.Vrm;

/// <summary>
/// VRM 的 GL 渲染器:CPU 蒙皮完成后的静态顶点(pos3/normal3/uv2)按材质分组绘制。
/// 光照用 MToon 风格的简化 half-lambert;支持 OPAQUE/MASK/BLEND、unlit(KHR_materials_unlit)与双面。
/// 数学约定:矩阵为 glTF 列向量语义(平移第 4 列),上传时转置(GL.UniformMatrix4 false)。
/// 与 SpineGLRenderer 相同的使用约定:在 GLControl 的 OnLoad 里 Init,每帧 RenderFrame。
/// </summary>
public sealed class VrmGLRenderer
{
    private int _program;
    private readonly Dictionary<int, int> _textures = new(); // VrmTexture 序号 → GL 纹理
    private readonly Dictionary<VrmPrimitive, (int vbo, int ebo, int count)> _buffers = new();
    private bool _initialized;
    private int _shadingMode = 2; // 0=toon(anime) 1=pbr(realistic) 2=mtoon(official)
    private int _uMvp, _uModel, _uLight, _uTint, _uCutoff, _uUnlit, _uTex;
    private int _uCamPos, _uShading, _uOutlinePass, _uOutlineWidth, _uOutlineColor, _uShadeColor, _uShadeShift, _uShadeToony, _uRimPower, _uRimLift;
    private int _uMatcap, _uMatcapColor, _uHasMatcap, _uRimColor;
    public int[] BlackTextureIndices { get; private set; } = Array.Empty<int>(); // 全黑占位贴图序号(诊断用)

    private const string VsSource = @"
#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uMvp;
uniform mat4 uModel;
uniform float uOutlineWidth; // >0: outline pass (extrude along normal)
out vec3 vNormal;
out vec2 vUv;
out vec3 vWorldPos;
void main()
{
    vec3 pos = aPos + aNormal * uOutlineWidth;
    vNormal = mat3(uModel) * aNormal;
    vUv = aUv;
    // position and normal MUST share the same uModel: skinned primitives arrive
    // in world space from CPU skinning (uModel = identity), non-skinned get the
    // node world matrix; a mismatch leaves normals in file space while the
    // rendered position is rotated -> facing surfaces saturate fresnel (white)
    vec4 wp = uModel * vec4(pos, 1.0);
    vWorldPos = wp.xyz;
    gl_Position = uMvp * wp;
}";

    private const string FsSource = @"
#version 330 core
in vec3 vNormal;
in vec2 vUv;
in vec3 vWorldPos;
uniform sampler2D uTex;
uniform sampler2D uMatcap;
uniform vec4 uMatcapColor;
uniform int uHasMatcap;
uniform vec3 uLight;
uniform vec3 uCamPos;
uniform vec4 uTint;
uniform float uCutoff;
uniform int uBlend;   // 0 = opaque/mask, 1 = blend
uniform int uUnlit;   // 1 = KHR_materials_unlit (only honored in mtoon/official style)
uniform int uShading; // 0 = toon (anime), 1 = pbr (realistic), 2 = mtoon (official)
uniform int uOutlinePass;
uniform vec4 uOutlineColor;
uniform vec4 uShadeColor;
uniform float uShadeShift;
uniform float uShadeToony;
uniform float uRimPower;
uniform float uRimLift;
uniform vec3 uRimColor;
out vec4 outColor;
void main()
{
    vec4 c = texture(uTex, vUv) * uTint;
    if (uOutlinePass == 1)
    {
        if (uBlend == 0 && c.a < uCutoff) discard;
        outColor = vec4(uOutlineColor.rgb, 1.0);
        return;
    }
    if (uBlend == 0 && c.a < uCutoff) discard;

    vec3 N = normalize(vNormal);
    vec3 V = normalize(uCamPos - vWorldPos);
    float ndl = dot(N, normalize(uLight));

    // unlit (non-mtoon) materials render flat in the toon/mtoon presets;
    // pbr is a host override and always lights
    if (uUnlit == 1 && uShading != 1)
    {
        outColor = vec4(c.rgb, c.a);
        return;
    }

    vec3 color;
    if (uShading == 1)
    {
        // realistic: lambert + blinn-phong spec + ambient
        float diff = max(ndl, 0.0);
        float amb = 0.32;
        vec3 H = normalize(normalize(uLight) + V);
        float spec = pow(max(dot(N, H), 0.0), 40.0) * 0.32;
        color = c.rgb * (amb + diff * 0.72) + vec3(spec);
        float rim = pow(1.0 - max(dot(N, V), 0.0), 4.0) * 0.18;
        color += rim;
    }
    else
    {
        // both toon and official are MToon, differing in the shade curve and rim modulation
        float fres = 1.0 - max(dot(N, V), 0.0);
        float rim = pow(clamp(fres + uRimLift, 0.0, 1.0), uRimPower);
        if (uShading == 0)
        {
            // anime: hard-edged MToon (raw-ndl threshold + rim not modulated by light)
            float litF = smoothstep(uShadeShift, uShadeShift + max(uShadeToony, 0.06), ndl);
            color = mix(uShadeColor.rgb * c.rgb, c.rgb, litF);
            color += rim * uRimColor;
        }
        else
        {
            // official: faithful soft MToon (half-lambert shade + light-modulated rim)
            float halfLambert = ndl * 0.5 + 0.5;
            float center = 0.5 + uShadeShift;
            float w = max(0.03, 0.5 * (1.0 - uShadeToony));
            float grade = smoothstep(center - w, center + w, halfLambert);
            color = mix(uShadeColor.rgb * c.rgb, c.rgb, grade);
            color += uRimColor * rim * clamp(halfLambert * 1.2, 0.0, 1.0);
        }
        // matcap: additive blended with lighting per VRM1 spec
        // (an all-black matcap placeholder contributes nothing, as per spec;
        //  r/g/b/a are reserved swizzle identifiers in GLSL, not variable names)
        if (uHasMatcap == 1)
        {
            vec3 right = normalize(cross(-V, vec3(0.0, 1.0, 0.0)));
            vec3 up = cross(V, right);
            vec2 mcuv = vec2(dot(N, right), dot(N, up)) * 0.5 + 0.5;
            color += texture(uMatcap, mcuv).rgb * uMatcapColor.rgb;
        }
    }
    outColor = vec4(color, c.a);
}";

    /// <summary>渲染风格:0 = toon 二次元,1 = pbr 写实,2 = mtoon 官方。</summary>
    public void SetShading(int mode) => _shadingMode = Math.Clamp(mode, 0, 2);

    public void Init()
    {
        if (_initialized)
            return;
        _program = BuildProgram(VsSource, FsSource);
        _uMvp = GL.GetUniformLocation(_program, "uMvp");
        _uModel = GL.GetUniformLocation(_program, "uModel");
        _uLight = GL.GetUniformLocation(_program, "uLight");
        _uTint = GL.GetUniformLocation(_program, "uTint");
        _uCutoff = GL.GetUniformLocation(_program, "uCutoff");
        _uUnlit = GL.GetUniformLocation(_program, "uUnlit");
        _uTex = GL.GetUniformLocation(_program, "uTex");
        _uCamPos = GL.GetUniformLocation(_program, "uCamPos");
        _uShading = GL.GetUniformLocation(_program, "uShading");
        _uOutlinePass = GL.GetUniformLocation(_program, "uOutlinePass");
        _uOutlineWidth = GL.GetUniformLocation(_program, "uOutlineWidth");
        _uOutlineColor = GL.GetUniformLocation(_program, "uOutlineColor");
        _uShadeColor = GL.GetUniformLocation(_program, "uShadeColor");
        _uShadeShift = GL.GetUniformLocation(_program, "uShadeShift");
        _uShadeToony = GL.GetUniformLocation(_program, "uShadeToony");
        _uRimPower = GL.GetUniformLocation(_program, "uRimPower");
        _uRimLift = GL.GetUniformLocation(_program, "uRimLift");
        _uMatcap = GL.GetUniformLocation(_program, "uMatcap");
        _uMatcapColor = GL.GetUniformLocation(_program, "uMatcapColor");
        _uHasMatcap = GL.GetUniformLocation(_program, "uHasMatcap");
        _uRimColor = GL.GetUniformLocation(_program, "uRimColor");
        var missing = new List<string>();
        CheckLoc("uMvp", _uMvp); CheckLoc("uModel", _uModel); CheckLoc("uLight", _uLight);
        CheckLoc("uTint", _uTint); CheckLoc("uCutoff", _uCutoff); CheckLoc("uUnlit", _uUnlit);
        CheckLoc("uTex", _uTex); CheckLoc("uCamPos", _uCamPos); CheckLoc("uShading", _uShading);
        CheckLoc("uOutlinePass", _uOutlinePass); CheckLoc("uOutlineWidth", _uOutlineWidth);
        CheckLoc("uOutlineColor", _uOutlineColor); CheckLoc("uShadeColor", _uShadeColor);
        CheckLoc("uShadeShift", _uShadeShift); CheckLoc("uShadeToony", _uShadeToony);
        CheckLoc("uRimPower", _uRimPower); CheckLoc("uRimLift", _uRimLift);
        CheckLoc("uMatcap", _uMatcap); CheckLoc("uMatcapColor", _uMatcapColor);
        CheckLoc("uHasMatcap", _uHasMatcap); CheckLoc("uRimColor", _uRimColor);
        if (missing.Count > 0)
            throw new InvalidOperationException("VRM shader missing uniforms: " + string.Join(",", missing));
        void CheckLoc(string name, int loc) { if (loc < 0) missing.Add(name); }
        GL.UseProgram(_program);
        GL.Uniform1(_uMatcap, 1); // matcap 固定纹理单元 1
        GL.UseProgram(0);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.Blend);
        GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
        GL.ClearColor(0f, 0f, 0f, 0f);
        _initialized = true;
    }

    private static int BuildProgram(string vs, string fs)
    {
        var program = GL.CreateProgram();
        var v = Compile(ShaderType.VertexShader, vs);
        GL.AttachShader(program, v);
        var f = Compile(ShaderType.FragmentShader, fs);
        GL.AttachShader(program, f);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException("VRM shader link failed: " + GL.GetProgramInfoLog(program));
        GL.DetachShader(program, v);
        GL.DetachShader(program, f);
        GL.DeleteShader(v);
        GL.DeleteShader(f);
        return program;
    }

    private static int Compile(ShaderType type, string source)
    {
        var s = GL.CreateShader(type);
        GL.ShaderSource(s, source);
        GL.CompileShader(s);
        GL.GetShader(s, ShaderParameter.CompileStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException($"VRM {type} compile failed:\n{GL.GetShaderInfoLog(s)}");
        return s;
    }

    /// <summary>模型加载诊断(贴图尺寸/均值 + 材质标志),一次性写入 diag 日志,便于排查异常 VRM。</summary>
    public string Diagnostics { get; private set; } = "";

    private static string BuildDiagnostics(VrmModel model, List<int> blacks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in model.Textures)
        {
            var b = t.Bgra;
            long r = 0, g = 0, bl = 0; var cnt = 0;
            for (var j = 0; j + 3 < b.Length; j += 16) { r += b[j + 2]; g += b[j + 1]; bl += b[j]; cnt++; }
            sb.Append($"t({t.Width}x{t.Height}~{r / Math.Max(1, cnt)},{g / Math.Max(1, cnt)},{bl / Math.Max(1, cnt)}) ");
        }
        sb.AppendLine();
        for (var i = 0; i < model.Materials.Count; i++)
        {
            var m = model.Materials[i];
            sb.Append($"[{i}]{m.Name} tex={m.TextureIndex} unlit={m.Unlit} mtoon={m.IsMToon} bcf=({m.BaseColorFactor.X:F2},{m.BaseColorFactor.Y:F2},{m.BaseColorFactor.Z:F2}) mc={m.MatcapTextureIndex} ");
        }
        sb.Append($"black={string.Join(",", blacks)}");
        return sb.ToString();
    }

    /// <summary>上传模型资源(纹理/缓冲);模型换载时调用(旧缓冲随之释放)。</summary>
    public void SetModel(VrmModel model)
    {
        foreach (var t in _textures.Values)
            GL.DeleteTexture(t);
        _textures.Clear();
        for (var i = 0; i < model.Textures.Count; i++)
        {
            var tex = model.Textures[i];
            var handle = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, handle);
            GL.TexImage2D<byte>(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, tex.Width, tex.Height, 0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, tex.Bgra);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _textures[i] = handle;
        }
        // 诊断:记录全黑占位贴图序号(部分 VRM 转换工具会把贴图替换成纯黑/占位图)
        var blacks = new List<int>();
        for (var i = 0; i < model.Textures.Count; i++)
        {
            var b = model.Textures[i].Bgra;
            var mx = 0;
            for (var j = 0; j + 3 < b.Length; j += 4)
            {
                if (b[j] > mx) mx = b[j];
                if (b[j + 1] > mx) mx = b[j + 1];
                if (b[j + 2] > mx) mx = b[j + 2];
                if (mx >= 8) break;
            }
            if (mx < 8) blacks.Add(i);
        }
        BlackTextureIndices = blacks.ToArray();
        Diagnostics = BuildDiagnostics(model, blacks);
        foreach (var (_, buf) in _buffers)
        {
            GL.DeleteBuffer(buf.vbo);
            GL.DeleteBuffer(buf.ebo);
        }
        _buffers.Clear();
        foreach (var p in model.Primitives)
        {
            var vbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, p.Skinned.Length * sizeof(float), p.Skinned, BufferUsageHint.DynamicDraw);
            var ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, p.Indices.Length * sizeof(ushort), p.Indices, BufferUsageHint.StaticDraw);
            _buffers[p] = (vbo, ebo, p.Indices.Length);
        }
    }

    /// <summary>把 stage 最新蒙皮结果上传到各 primitive 的动态 VBO(每帧绘制前调用)。</summary>
    public void UploadSkinned(VrmModel model)
    {
        foreach (var p in model.Primitives)
        {
            if (!_buffers.TryGetValue(p, out var buf))
                continue;
            GL.BindBuffer(BufferTarget.ArrayBuffer, buf.vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, p.Skinned.Length * sizeof(float), p.Skinned);
        }
    }

    /// <summary>一帧绘制:相机矩阵由窗体按模型包围盒构造(数学列向量)。</summary>
    public void RenderFrame(VrmModel model, Matrix4 viewProjection, Vector3 lightDir, Vector3 camPos)
    {
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        UploadSkinned(model);
        GL.UseProgram(_program);
        GL.Uniform3(_uLight, lightDir);
        GL.Uniform3(_uCamPos, camPos);
        GL.Uniform1(_uShading, _shadingMode);

        bool IsBlend(VrmPrimitive p) =>
            p.MaterialIndex >= 0 && model.Materials[p.MaterialIndex].AlphaMode == "BLEND";
        var opaque = model.Primitives.Where(p => !IsBlend(p)).ToList();
        var transparent = model.Primitives.Where(IsBlend).ToList();

        GL.Disable(EnableCap.CullFace);
        foreach (var p in opaque)
            DrawPrimitive(model, p, viewProjection);
        foreach (var p in transparent)
            DrawPrimitive(model, p, viewProjection);


        // 描边 pass(二次元/官方):启用描边的材质,沿法线外扩,只画背面
        if (_shadingMode != 1)
        {
            GL.Uniform1(_uOutlinePass, 1);
            // 主 pass 为双面渲染关掉了剔除;描边必须重新启用,
            // 否则前向面也会被外扩后画在主 pass 之上,整片盖住模型
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Front);
            foreach (var p in opaque.Concat(transparent))
            {
                if (p.MaterialIndex < 0 || p.MaterialIndex >= model.Materials.Count) continue;
                var outlineW = model.Materials[p.MaterialIndex].OutlineWidth;
                if (outlineW <= 0) continue;
                GL.Uniform1(_uOutlineWidth, outlineW);
                var oc = model.Materials[p.MaterialIndex].ShadeColor;
                GL.Uniform4(_uOutlineColor, new Vector4(oc.X * 0.35f, oc.Y * 0.35f, oc.Z * 0.35f, 1f));
                DrawPrimitive(model, p, viewProjection, outlineW);
            }
            GL.CullFace(TriangleFace.Back);
            GL.Uniform1(_uOutlinePass, 0);
        }
        GL.Enable(EnableCap.CullFace);
    }

    private void DrawPrimitive(VrmModel model, VrmPrimitive p, Matrix4 viewProjection, float outlineWidth = 0f)
    {
        if (!_buffers.TryGetValue(p, out var buf) || p.MaterialIndex < 0 || p.MaterialIndex >= model.Materials.Count)
            return;
        var material = model.Materials[p.MaterialIndex];
        var blend = material.AlphaMode == "BLEND";
        if (blend)
        {
            GL.DepthMask(false);
            GL.Uniform1(_uCutoff, 0f);
        }
        else
        {
            GL.DepthMask(true);
            GL.Uniform1(_uCutoff, material.AlphaMode == "MASK" ? material.AlphaCutoff : 0f);
        }

        // glTF 规范:蒙皮 primitive 的关节矩阵已变换到世界空间,节点变换不得再叠加(uModel=单位阵);
        // 非蒙皮 primitive 才需要节点世界矩阵
        var isSkinned = p.SkinJoints.Length > 0 && p.Joints.Length > 0;
        var modelMat = (!isSkinned && p.Node >= 0) ? model.Nodes[p.Node].World : Matrix4.Identity;
        // 数学(列向量)矩阵转置后上传:GL 按 false(列主序)读取时,GLSL 得到数学矩阵本身
        var mvpT = Transpose(viewProjection);
        var modelT = Transpose(modelMat);
        GL.UniformMatrix4(_uMvp, false, ref mvpT);
        GL.UniformMatrix4(_uModel, false, ref modelT);
        GL.Uniform4(_uTint, material.BaseColorFactor);
        GL.Uniform1(_uUnlit, material.Unlit ? 1 : 0);
        GL.Uniform1(_uOutlineWidth, outlineWidth);
        GL.Uniform4(_uShadeColor, material.ShadeColor);
        GL.Uniform1(_uShadeShift, material.ShadeShift);
        GL.Uniform1(_uShadeToony, material.ShadeToony);
        GL.Uniform1(_uRimPower, material.RimFresnelPower);
        GL.Uniform1(_uRimLift, material.RimLift);
        GL.Uniform3(_uRimColor, material.RimColor.X, material.RimColor.Y, material.RimColor.Z);
        if (_textures.TryGetValue(material.TextureIndex, out var tex))
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, tex);
        }
        // matcap 环境贴图(mtoon 官方风格用);绑定到 texture unit 1
        if (material.MatcapTextureIndex >= 0 && _textures.TryGetValue(material.MatcapTextureIndex, out var mc))
        {
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, mc);
            GL.Uniform1(_uHasMatcap, 1);
            GL.Uniform4(_uMatcapColor, material.MatcapColor);
        }
        else
        {
            GL.Uniform1(_uHasMatcap, 0);
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, buf.vbo);
        var stride = 8 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, buf.ebo);
        GL.DrawElements(PrimitiveType.Triangles, buf.count, DrawElementsType.UnsignedShort, 0);
        GL.DepthMask(true);
    }

    private static Matrix4 Transpose(Matrix4 m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44);

    public void Dispose()
    {
        foreach (var t in _textures.Values)
            GL.DeleteTexture(t);
        _textures.Clear();
        foreach (var (_, buf) in _buffers)
        {
            GL.DeleteBuffer(buf.vbo);
            GL.DeleteBuffer(buf.ebo);
        }
        _buffers.Clear();
        if (_program != 0)
            GL.DeleteProgram(_program);
        _program = 0;
        _initialized = false;
    }
}
