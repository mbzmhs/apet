using System.Text.Json;
using OpenTK.Mathematics;

namespace APet.Renderer.Vrm;

// ───────── 加载后的静态数据(VrmModel)与解析(VrmLoader) ─────────

public sealed class VrmNode
{
    public required string Name;
    public required int Index;
    public int Parent = -1;
    public readonly List<int> Children = new();
    public Vector3 LocalTranslation;
    public Quaternion LocalRotation = Quaternion.Identity;
    public Vector3 LocalScale = Vector3.One;
    public int Mesh = -1;          // 节点挂载的 glTF mesh
    public int Skin = -1;          // mesh 的 skin
    public Matrix4 World = Matrix4.Identity;
}

public sealed class VrmMaterial
{
    public required string Name;
    public int TextureIndex = -1;
    public string AlphaMode = "OPAQUE"; // OPAQUE / MASK / BLEND
    public float AlphaCutoff = 0.5f;
    public bool DoubleSided;
    public bool Unlit; // KHR_materials_unlit(非 MToon 材质):官方风格下跳过光照直接显示贴图
    public bool IsMToon; // VRMC_materials_mtoon / VRM0 VRM/MToon
    public Vector4 BaseColorFactor = Vector4.One;

    // MToon 参数(VRM0/1 materialProperties;缺省用官方观感的近似值)
    public Vector4 ShadeColor = new(0.85f, 0.85f, 0.9f, 1f); // 阴影区颜色
    public float ShadeShift = -0.6f;   // 阴影阈值偏移(越小阴影越少)
    public float ShadeToony = 0.5f;    // 0=软过渡,1=硬分档
    public float RimFresnelPower = 6f; // 边缘光锐度
    public float RimLift = 0.08f;      // 边缘光抬升
    public Vector4 RimColor = Vector4.One; // parametricRimColorFactor(0=无边缘光)
    public float OutlineWidth;         // 描边宽度(米;0=无描边)
    public int MatcapTextureIndex = -1; // unlit 时的环境贴图;无则纯贴图
    public Vector4 MatcapColor = Vector4.One; // matcapFactor
}

/// <summary>一个可绘制单元(mesh primitive):基础顶点 + morph targets + 骨骼绑定。</summary>
public sealed class VrmPrimitive
{
    public required int Node;               // 挂载节点(取 world 与 skin)
    public required int Skin;
    public int SourceMesh;                  // 来自哪个 glTF mesh(VRM0 blendShape bind 按 mesh 索引)
    public int MaterialIndex = -1;
    public Vector3[] BasePositions = Array.Empty<Vector3>();
    public Vector3[] BaseNormals = Array.Empty<Vector3>();
    public Vector2[] Uvs = Array.Empty<Vector2>();
    public Vector4[] Joints = Array.Empty<Vector4>();   // 分量为骨骼在 skin.joints 里的序号
    public Vector4[] Weights = Array.Empty<Vector4>();
    public ushort[] Indices = Array.Empty<ushort>();
    // morph targets(位移 delta):与 glTF primitive.targets 对应
    public Vector3[][] TargetPositions = Array.Empty<Vector3[]>();
    public Vector3[][] TargetNormals = Array.Empty<Vector3[]>();
    public Matrix4[] InverseBinds = Array.Empty<Matrix4>();
    public int[] SkinJoints = Array.Empty<int>();
    // 蒙皮输出(每帧由 stage 重写,renderer 上传)
    public float[] Skinned = Array.Empty<float>();      // 交错 pos(3) normal(3) uv(2)
}

public sealed class VrmTexture
{
    public required byte[] Bgra;
    public required int Width;
    public required int Height;
}

public sealed class VrmExpression
{
    public required string Key;
    public required string Category; // emotion / mouth / blink / look / other
    public readonly List<(int primitive, int target, float weight)> MorphBinds = new();
}

public sealed class VrmSpringJoint
{
    public required int Node;
    public float Stiffness = 1.0f;
    public float DragForce = 0.4f;
    public float GravityPower;
    public Vector3 GravityDir = new(0f, -1f, 0f);
    public float HitRadius = 0.05f;
    public int TailNode = -1;               // 链中下一个节点(模拟的目标点);-1 = 无子(用同向延伸)
    public Vector3 Current, Previous;       // tail 的世界空间模拟位置
    public bool Initialized;
}

public sealed class VrmSpringCollider
{
    public required int Node;
    public required float Radius;
    public Vector3 Offset;
}

public sealed class VrmSpringGroup
{
    public int Center = -1;
    public readonly List<VrmSpringJoint> Joints = new();
    public readonly List<VrmSpringCollider> Colliders = new();
}

public sealed class VrmModel
{
    public required List<VrmNode> Nodes;
    public required List<VrmPrimitive> Primitives;
    public required List<VrmMaterial> Materials;
    public required List<VrmTexture> Textures;
    public required Dictionary<string, int> HumanBones;             // humanoid 名 → 节点序号
    public required Dictionary<string, VrmExpression> Expressions;  // 表情 key → 绑定
    public required List<VrmSpringGroup> SpringGroups;
    public required Vector3 BoundsMin;
    public required Vector3 BoundsMax;
    public required string MetaTitle;
    public int SpecVersion; // VRM 世代:0 / 1

    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public Vector3 Size => BoundsMax - BoundsMin;
}

/// <summary>VRM(vrm 0.x / 1.0)加载器:GLB 容器解析 + VRM 扩展(humanoid/表情/物理)。</summary>
public static class VrmLoader
{
    public static VrmModel Load(string path)
    {
        using var src = GltfSource.Load(path);
        return Load(src);
    }

    public static VrmModel Load(GltfSource src)
    {
        var root = src.Root;
        var nodes = ParseNodes(root);
        var materials = ParseMaterials(root);
        var textures = LoadTextures(src);
        var primitives = ParsePrimitives(src, nodes, materials.Count, textures.Count);

        ParseVrmMaterialProperties(src, materials);
        var humanoid = ParseHumanoid(root);
        var expressions = ParseExpressions(src, primitives);
        var springs = ParseSprings(root, nodes, humanoid);

        // 静止姿势包围盒:全部 primitive 的基础顶点
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in primitives)
        {
            foreach (var v in p.BasePositions)
            {
                min = Vector3.ComponentMin(min, v);
                max = Vector3.ComponentMax(max, v);
            }
        }
        if (min.X > max.X) { min = new Vector3(-0.25f, 0f, -0.15f); max = new Vector3(0.25f, 1.4f, 0.15f); }

        string title = "VRM";
        if (root.TryGetProperty("extensions", out var exts))
        {
            if (exts.TryGetProperty("VRM", out var v0) && v0.TryGetProperty("meta", out var m0))
                title = m0.TryGetProperty("title", out var t) ? t.GetString() ?? title : title;
            if (exts.TryGetProperty("VRMC_vrm", out var v1) && v1.TryGetProperty("meta", out var m1))
                title = m1.TryGetProperty("name", out var t) ? t.GetString() ?? title : title;
        }

        return new VrmModel
        {
            SpecVersion = exts.TryGetProperty("VRMC_vrm", out _) ? 1 : 0,
            Nodes = nodes,
            Primitives = primitives,
            Materials = materials,
            Textures = textures,
            HumanBones = humanoid,
            Expressions = expressions,
            SpringGroups = springs,
            BoundsMin = min,
            BoundsMax = max,
            MetaTitle = title,
        };
    }

    // ───────── 节点层级 ─────────

    private static List<VrmNode> ParseNodes(JsonElement root)
    {
        var arr = root.GetProperty("nodes");
        var nodes = new List<VrmNode>(arr.GetArrayLength());
        for (var i = 0; i < arr.GetArrayLength(); i++)
        {
            var n = arr[i];
            var node = new VrmNode
            {
                Index = i,
                Name = n.TryGetProperty("name", out var nm) ? nm.GetString() ?? $"node{i}" : $"node{i}",
            };
            if (n.TryGetProperty("translation", out var t))
                node.LocalTranslation = new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle());
            if (n.TryGetProperty("rotation", out var r))
                node.LocalRotation = new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
            if (n.TryGetProperty("scale", out var s))
                node.LocalScale = new Vector3(s[0].GetSingle(), s[1].GetSingle(), s[2].GetSingle());
            if (n.TryGetProperty("mesh", out var mesh))
                node.Mesh = mesh.GetInt32();
            if (n.TryGetProperty("skin", out var skin))
                node.Skin = skin.GetInt32();
            nodes.Add(node);
        }
        for (var i = 0; i < arr.GetArrayLength(); i++)
        {
            if (!arr[i].TryGetProperty("children", out var children))
                continue;
            foreach (var c in children.EnumerateArray())
            {
                var child = c.GetInt32();
                nodes[i].Children.Add(child);
                nodes[child].Parent = i;
            }
        }
        return nodes;
    }

    // ───────── 材质 / 贴图 ─────────

    private static List<VrmMaterial> ParseMaterials(JsonElement root)
    {
        var list = new List<VrmMaterial>();
        if (!root.TryGetProperty("materials", out var arr))
            return list;
        foreach (var m in arr.EnumerateArray())
        {
            var mat = new VrmMaterial
            {
                Name = m.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
            };
            if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorTexture", out var bct))
                    mat.TextureIndex = bct.GetProperty("index").GetInt32();
                if (pbr.TryGetProperty("baseColorFactor", out var f))
                    mat.BaseColorFactor = new Vector4(f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle(), f[3].GetSingle());
            }
            // UniVRM/MToon 导出:主贴图常放 emissiveTexture(unlit),无 baseColorTexture
            if (mat.TextureIndex < 0 && m.TryGetProperty("emissiveTexture", out var et))
                mat.TextureIndex = et.GetProperty("index").GetInt32();
            if (m.TryGetProperty("extensions", out var mext))
            {
                // VRM1:MToon 参数在材质级 extensions.VRMC_materials_mtoon
                if (mext.TryGetProperty("VRMC_materials_mtoon", out var mtoon))
                {
                    mat.IsMToon = true;
                    mat.Unlit = false; // 规范:MToon 与 KHR_materials_unlit 并存时 MToon 优先(按光照渲染)
                    // 旧 UniVRM 格式(floatProperties/vectorProperties)
                    if (mtoon.TryGetProperty("floatProperties", out var fp))
                    {
                        float F2(string key, float def) =>
                            fp.TryGetProperty(key, out var f) ? f.GetSingle() : def;
                        mat.ShadeShift = F2("_ShadeShift", -0.6f);
                        mat.ShadeToony = Math.Clamp(F2("_ShadeToony", 0.5f), 0f, 1f);
                        mat.RimFresnelPower = MathF.Max(0.5f, F2("_RimFresnelPower", 6f));
                        mat.RimLift = F2("_RimLift", 0.08f);
                        mat.OutlineWidth = F2("_OutlineWidth", 0f);
                        if (mat.OutlineWidth > 0 && F2("_OutlineWidthMode", 0f) == 0f)
                            mat.OutlineWidth = 0f;
                    }
                    if (mtoon.TryGetProperty("vectorProperties", out var vp2) && vp2.TryGetProperty("_ShadeColorFactor", out var sc2))
                    {
                        var sc = ParseVec3(sc2);
                        mat.ShadeColor = new Vector4(sc.X, sc.Y, sc.Z, 1f);
                    }
                    // VRM1 规范格式(shadingShiftFactor 等),存在时覆盖旧格式
                    if (mtoon.TryGetProperty("shadingShiftFactor", out var ss))
                        mat.ShadeShift = ss.GetSingle();
                    if (mtoon.TryGetProperty("shadingToonyFactor", out var st))
                        mat.ShadeToony = Math.Clamp(st.GetSingle(), 0f, 1f);
                    if (mtoon.TryGetProperty("parametricRimFresnelPowerFactor", out var rp))
                        mat.RimFresnelPower = MathF.Max(0.5f, rp.GetSingle());
                    if (mtoon.TryGetProperty("parametricRimLiftFactor", out var rl))
                        mat.RimLift = rl.GetSingle();
                    if (mtoon.TryGetProperty("shadeColorFactor", out var scf))
                    {
                        var sc = ParseVec3(scf);
                        mat.ShadeColor = new Vector4(sc.X, sc.Y, sc.Z, 1f);
                    }
                    if (mtoon.TryGetProperty("parametricRimColorFactor", out var rcf))
                    {
                        var rc = ParseVec3(rcf);
                        mat.RimColor = new Vector4(rc.X, rc.Y, rc.Z, 1f);
                    }
                    if (mtoon.TryGetProperty("outlineWidthMode", out var owm) && owm.GetString() == "worldCoordinates"
                        && mtoon.TryGetProperty("outlineWidthFactor", out var owf))
                        mat.OutlineWidth = owf.GetSingle();
                    if (mtoon.TryGetProperty("matcapTexture", out var mct) && mct.TryGetProperty("index", out var mci))
                        mat.MatcapTextureIndex = mci.GetInt32();
                    if (mtoon.TryGetProperty("matcapFactor", out var mcf) && mcf.GetArrayLength() == 3)
                        mat.MatcapColor = new Vector4(mcf[0].GetSingle(), mcf[1].GetSingle(), mcf[2].GetSingle(), 1f);
                }
                // KHR_materials_unlit:仅对非 MToon 材质生效(官方风格直接显示贴图;
                // 二次元/写实风格是宿主样式覆盖,不受此扩展影响)
                if (mext.TryGetProperty("KHR_materials_unlit", out _) && !mat.IsMToon)
                    mat.Unlit = true;
            }
            if (m.TryGetProperty("alphaMode", out var am))
                mat.AlphaMode = am.GetString() ?? "OPAQUE";
            if (m.TryGetProperty("alphaCutoff", out var ac))
                mat.AlphaCutoff = ac.GetSingle();
            if (m.TryGetProperty("doubleSided", out var ds))
                mat.DoubleSided = ds.GetBoolean();
            list.Add(mat);
        }
        return list;
    }

    private static List<VrmTexture> LoadTextures(GltfSource src)
    {
        var list = new List<VrmTexture>();
        if (!src.Root.TryGetProperty("images", out var images))
            return list;
        // textures[] 的 source 指向 images;贴图按 textures 序号提供
        if (src.Root.TryGetProperty("textures", out var texs))
        {
            foreach (var t in texs.EnumerateArray())
            {
                var imgIdx = t.TryGetProperty("source", out var s) ? s.GetInt32() : 0;
                var (bgra, w, h) = src.DecodeImage(imgIdx);
                list.Add(new VrmTexture { Bgra = bgra, Width = w, Height = h });
            }
        }
        else
        {
            foreach (var _ in images.EnumerateArray())
            {
                var idx = list.Count;
                var (bgra, w, h) = src.DecodeImage(idx);
                list.Add(new VrmTexture { Bgra = bgra, Width = w, Height = h });
            }
        }
        return list;
    }

    // ───────── 网格 ─────────

    private static List<VrmPrimitive> ParsePrimitives(GltfSource src, List<VrmNode> nodes, int materialCount, int textureCount)
    {
        var root = src.Root;
        var list = new List<VrmPrimitive>();
        if (!root.TryGetProperty("meshes", out var meshes))
            return list;

        var meshIndex = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            if (!mesh.TryGetProperty("primitives", out var prims))
            {
                meshIndex++;
                continue;
            }
            foreach (var p in prims.EnumerateArray())
            {
                var prim = new VrmPrimitive { Node = -1, Skin = -1, SourceMesh = meshIndex };
                if (p.TryGetProperty("material", out var mi))
                    prim.MaterialIndex = Math.Min(mi.GetInt32(), materialCount - 1);
                if (p.TryGetProperty("attributes", out var attrs))
                {
                    prim.BasePositions = ToVector3(src.ReadFloats(attrs.GetProperty("POSITION").GetInt32()));
                    if (attrs.TryGetProperty("NORMAL", out var n))
                        prim.BaseNormals = ToVector3(src.ReadFloats(n.GetInt32()));
                    if (attrs.TryGetProperty("TEXCOORD_0", out var uv))
                    {
                        var uvf = src.ReadFloats(uv.GetInt32());
                        prim.Uvs = new Vector2[uvf.Length / 2];
                        for (var i = 0; i < prim.Uvs.Length; i++)
                            prim.Uvs[i] = new Vector2(uvf[i * 2], uvf[i * 2 + 1]);
                    }
                    if (attrs.TryGetProperty("JOINTS_0", out var j))
                    {
                        var jf = src.ReadFloats(j.GetInt32());
                        prim.Joints = new Vector4[jf.Length / 4];
                        for (var i = 0; i < prim.Joints.Length; i++)
                            prim.Joints[i] = new Vector4(jf[i * 4], jf[i * 4 + 1], jf[i * 4 + 2], jf[i * 4 + 3]);
                    }
                    if (attrs.TryGetProperty("WEIGHTS_0", out var w))
                    {
                        var wf = src.ReadFloats(w.GetInt32());
                        prim.Weights = new Vector4[wf.Length / 4];
                        for (var i = 0; i < prim.Weights.Length; i++)
                            prim.Weights[i] = new Vector4(wf[i * 4], wf[i * 4 + 1], wf[i * 4 + 2], wf[i * 4 + 3]);
                    }
                }
                if (p.TryGetProperty("indices", out var ii))
                    prim.Indices = src.ReadIndices(ii.GetInt32());
                if (p.TryGetProperty("targets", out var targets))
                {
                    var posList = new List<Vector3[]>();
                    var normList = new List<Vector3[]>();
                    foreach (var t in targets.EnumerateArray())
                    {
                        posList.Add(t.TryGetProperty("POSITION", out var tp)
                            ? ToVector3(src.ReadFloats(tp.GetInt32()))
                            : Array.Empty<Vector3>());
                        normList.Add(t.TryGetProperty("NORMAL", out var tn)
                            ? ToVector3(src.ReadFloats(tn.GetInt32()))
                            : Array.Empty<Vector3>());
                    }
                    prim.TargetPositions = posList.ToArray();
                    prim.TargetNormals = normList.ToArray();
                }

                // 记录 node/skin:解析节点时补挂(挂 mesh 的节点)
                list.Add(prim);
            }
            meshIndex++;
        }

        foreach (var prim in list)
        {
            var node = nodes.FirstOrDefault(n => n.Mesh == prim.SourceMesh);
            if (node is null)
                continue;
            prim.Node = node.Index;
            prim.Skin = node.Skin;
        }

        // skin 数据(逆绑定矩阵与关节列表)
        if (root.TryGetProperty("skins", out var skins))
        {
            var skinData = new List<(int[] joints, Matrix4[] inv)>();
            foreach (var sk in skins.EnumerateArray())
            {
                var joints = sk.GetProperty("joints").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                Matrix4[] inv = Array.Empty<Matrix4>();
                if (sk.TryGetProperty("inverseBindMatrices", out var ibm))
                {
                    var f = src.ReadMatrices(ibm.GetInt32());
                    inv = new Matrix4[f.Length / 16];
                    for (var i = 0; i < inv.Length; i++)
                    {
                        // glTF 列主序 → OpenTK Matrix4(构造函数按行传入,注意排布)
                        var m = new Matrix4(
                            f[i * 16 + 0], f[i * 16 + 4], f[i * 16 + 8], f[i * 16 + 12],
                            f[i * 16 + 1], f[i * 16 + 5], f[i * 16 + 9], f[i * 16 + 13],
                            f[i * 16 + 2], f[i * 16 + 6], f[i * 16 + 10], f[i * 16 + 14],
                            f[i * 16 + 3], f[i * 16 + 7], f[i * 16 + 11], f[i * 16 + 15]);
                        inv[i] = m;
                    }
                }
                skinData.Add((joints, inv));
            }

            foreach (var prim in list)
            {
                if (prim.Skin < 0 || prim.Skin >= skinData.Count)
                    continue;
                var (joints, inv) = skinData[prim.Skin];
                prim.SkinJoints = joints;
                prim.InverseBinds = inv;
                if (prim.Joints.Length == 0)
                    continue;
                // 顶点的 JOINTS_0 是 skin.joints 的下标;为省一次查表,预换成节点序号
                for (var i = 0; i < prim.Joints.Length; i++)
                {
                    var v = prim.Joints[i];
                    prim.Joints[i] = new Vector4(
                        joints[(int)v.X], joints[(int)v.Y], joints[(int)v.Z], joints[(int)v.W]);
                }
            }
        }

        return list;
    }

    /// <summary>glTF/VRM 混合风格的三元组:数组 [x,y,z] 或对象 {x,y,z}。</summary>
    private static Vector3 ParseVec3(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
            return new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        return new Vector3(
            e.TryGetProperty("x", out var x) ? x.GetSingle() : 0f,
            e.TryGetProperty("y", out var y) ? y.GetSingle() : 0f,
            e.TryGetProperty("z", out var z) ? z.GetSingle() : 0f);
    }

    private static Vector3[] ToVector3(float[] f)
    {
        var v = new Vector3[f.Length / 3];
        for (var i = 0; i < v.Length; i++)
            v[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]);
        return v;
    }

    /// <summary>MToon 参数解析(VRM0 extensions.VRM.materialProperties / VRM1 VRMC_vrm.materialProperties):
    /// 阴影色/阴影阈值/边缘光/描边宽度。shader 非 MToon 时保留缺省。</summary>
    private static void ParseVrmMaterialProperties(GltfSource src, List<VrmMaterial> materials)
    {
        var root = src.Root;
        if (!root.TryGetProperty("extensions", out var exts))
            return;
        JsonElement props = default;
        var hasProps = false;
        if (exts.TryGetProperty("VRM", out var v0) && v0.TryGetProperty("materialProperties", out var p0))
        { props = p0; hasProps = true; }
        else if (exts.TryGetProperty("VRMC_vrm", out var v1) && v1.TryGetProperty("materialProperties", out var p1))
        { props = p1; hasProps = true; }
        if (!hasProps)
            return;

        foreach (var mp in props.EnumerateArray())
        {
            var name = mp.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            var mat = materials.FirstOrDefault(m => m.Name == name);
            if (mat is null)
                continue;
            var isMToon = false;
            if (mp.TryGetProperty("shader", out var sh) && (sh.GetString() ?? "").Contains("MToon"))
                isMToon = true;

            var hasFloats = mp.TryGetProperty("floatProperties", out var floats);
            var hasVectors = mp.TryGetProperty("vectorProperties", out var vectors);
            float F(string key, float def) =>
                hasFloats && floats.TryGetProperty(key, out var f) ? f.GetSingle() : def;

            if (isMToon)
            {
                // VRM0 的 MToon 材质只由 extensions.VRM.materialProperties 声明(无 VRMC_materials_mtoon);
                // 标记 IsMToon 并撤销 ParseMaterials 按 KHR_materials_unlit 误置的 unlit(MToon 优先,按光照渲染)
                mat.IsMToon = true;
                mat.Unlit = false;
                mat.ShadeShift = F("_ShadeShift", -0.6f);
                mat.ShadeToony = Math.Clamp(F("_ShadeToony", 0.5f), 0f, 1f);
                mat.RimFresnelPower = MathF.Max(0.5f, F("_RimFresnelPower", 6f));
                mat.RimLift = F("_RimLift", 0.08f);
                // 描边枚举:0=世界坐标(宽度按米外扩)/1=屏幕坐标/2=禁用。渲染器只有世界空间外扩路径,
                // 故仅 mode 0 取用宽度;屏幕坐标的值不是米,误当米外扩会糊出巨大灰壳(本模型 mode=1)
                mat.OutlineWidth = F("_OutlineWidthMode", 0f) == 0f ? F("_OutlineWidth", 0f) : 0f;
                // VRM0 向量键为 _ShadeColor/_RimColor(旧 UniVRM 才有 _ShadeColorFactor);
                // 不读 _RimColor 会留下默认白色边缘光,官方风格下整圈泛白
                if (hasVectors)
                {
                    if (vectors.TryGetProperty("_ShadeColor", out var scv) ||
                        vectors.TryGetProperty("_ShadeColorFactor", out scv))
                    {
                        var sc = ParseVec3(scv);
                        mat.ShadeColor = new Vector4(sc.X, sc.Y, sc.Z, 1f);
                    }
                    if (vectors.TryGetProperty("_RimColor", out var rcm))
                    {
                        var rc = ParseVec3(rcm);
                        mat.RimColor = new Vector4(rc.X, rc.Y, rc.Z, 1f);
                    }
                }
            }
        }
    }

    // ───────── VRM 扩展:humanoid / 表情 / 物理 ─────────

    private static Dictionary<string, int> ParseHumanoid(JsonElement root)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("extensions", out var exts))
            return map;
        if (exts.TryGetProperty("VRM", out var v0) && v0.TryGetProperty("humanoid", out var h0)
            && h0.TryGetProperty("humanBones", out var bones0))
        {
            foreach (var b in bones0.EnumerateArray())
            {
                var bone = b.GetProperty("bone").GetString();
                var node = b.GetProperty("node").GetInt32();
                if (bone is not null) map[NormalizeBoneName(bone)] = node;
            }
        }
        else if (exts.TryGetProperty("VRMC_vrm", out var v1) && v1.TryGetProperty("humanoid", out var h1)
                 && h1.TryGetProperty("humanBones", out var bones1))
        {
            foreach (var b in bones1.EnumerateObject())
            {
                if (b.Value.TryGetProperty("node", out var node))
                    map[b.Name] = node.GetInt32();
            }
        }
        return map;
    }

    /// <summary>VRM0 与 VRM1 的拇指标准名错位一格:同一物理骨骼(Thumb1/2/3),VRM0 叫
    /// Proximal/Intermediate/Distal,VRM1 叫 Metacarpal/Proximal/Distal。其余手指两代同名。
    /// 统一归一化为 VRM1 规范名,保证待机手指卷曲与 VRMA 动画通道在 VRM0 模型上按解剖学
    /// 正确的关节生效(否则卷曲/动画会错扭在拇指根部)。</summary>
    private static string NormalizeBoneName(string bone) => bone switch
    {
        "leftThumbProximal" => "leftThumbMetacarpal",
        "leftThumbIntermediate" => "leftThumbProximal",
        "rightThumbProximal" => "rightThumbMetacarpal",
        "rightThumbIntermediate" => "rightThumbProximal",
        _ => bone,
    };

    private static readonly HashSet<string> EmotionKeys = new(StringComparer.OrdinalIgnoreCase)
        { "happy", "angry", "sad", "relaxed", "surprised", "neutral", "joy", "fun", "sorrow" };
    private static readonly HashSet<string> MouthKeys = new(StringComparer.OrdinalIgnoreCase)
        { "aa", "ih", "ou", "ee", "oh", "a", "i", "u", "e", "o" };
    private static readonly HashSet<string> BlinkKeys = new(StringComparer.OrdinalIgnoreCase)
        { "blink", "blinkleft", "blinkright", "blink_l", "blink_r" };
    private static readonly HashSet<string> LookKeys = new(StringComparer.OrdinalIgnoreCase)
        { "lookup", "lookdown", "lookleft", "lookright" };

    private static Dictionary<string, VrmExpression> ParseExpressions(GltfSource src, List<VrmPrimitive> primitives)
    {
        var map = new Dictionary<string, VrmExpression>(StringComparer.OrdinalIgnoreCase);
        var root = src.Root;
        if (!root.TryGetProperty("extensions", out var exts))
            return map;

        // VRM 0.x:blendShapeMaster.blendShapeGroups
        if (exts.TryGetProperty("VRM", out var v0) && v0.TryGetProperty("blendShapeMaster", out var bsm)
            && bsm.TryGetProperty("blendShapeGroups", out var groups))
        {
            foreach (var g in groups.EnumerateArray())
            {
                var key = g.TryGetProperty("name", out var nm) && !string.IsNullOrEmpty(nm.GetString())
                    ? nm.GetString()!
                    : g.TryGetProperty("presetName", out var pn) ? pn.GetString() ?? "" : "";
                if (key.Length == 0)
                    continue;
                var normKey = NormalizeExpressionKey(key);
                var expr = new VrmExpression { Key = normKey, Category = CategoryOf(normKey) };
                if (g.TryGetProperty("binds", out var binds))
                {
                    foreach (var b in binds.EnumerateArray())
                    {
                        var meshIdx = b.GetProperty("mesh").GetInt32();
                        var morphIdx = b.GetProperty("index").GetInt32();
                        var weight = b.TryGetProperty("indexWeight", out var iw)
                            ? (iw.GetSingle() > 1f ? iw.GetSingle() / 100f : iw.GetSingle())
                            : 1f;
                        foreach (var prim in primitives.Where(p => p.SourceMesh == meshIdx))
                            expr.MorphBinds.Add((primitives.IndexOf(prim), morphIdx, weight));
                    }
                }
                map.TryAdd(NormalizeExpressionKey(key), expr);
            }
        }

        // VRM 1.0:expressions.preset / custom(morphTargetBinds 按 node)
        if (exts.TryGetProperty("VRMC_vrm", out var v1) && v1.TryGetProperty("expressions", out var ex))
        {
            foreach (var section in new[] { "preset", "custom" })
            {
                if (!ex.TryGetProperty(section, out var sec))
                    continue;
                foreach (var e in sec.EnumerateObject())
                {
                    var normKey = NormalizeExpressionKey(e.Name);
                    var expr = new VrmExpression { Key = normKey, Category = CategoryOf(normKey) };
                    if (e.Value.TryGetProperty("morphTargetBinds", out var binds))
                    {
                        foreach (var b in binds.EnumerateArray())
                        {
                            var node = b.GetProperty("node").GetInt32();
                            // 字段名兼容:morphTargetIndex(规范)/index(部分导出器)
                            var morphIdx = b.TryGetProperty("morphTargetIndex", out var mti) ? mti.GetInt32()
                                : b.GetProperty("index").GetInt32();
                            // 权重兼容:0..1 直接使用;>1 视为 0..100 百分数
                            var weight = b.TryGetProperty("weight", out var w)
                                ? (w.GetSingle() > 1f ? w.GetSingle() / 100f : w.GetSingle())
                                : 1f;
                            foreach (var p in primitives.Where(p => p.Node == node))
                                expr.MorphBinds.Add((primitives.IndexOf(p), morphIdx, weight));
                        }
                    }
                    map.TryAdd(normKey, expr);
                }
            }
        }
        return map;
    }

    /// <summary>表情 key 归一化:VRM0 旧命名(joy/fun/sorrow)→ VRM1 标准(happy/relaxed/sad),统一小写。</summary>
    private static string NormalizeExpressionKey(string key) => key.ToLowerInvariant() switch
    {
        "joy" => "happy",
        "fun" => "relaxed",
        "sorrow" => "sad",
        "smile" => "happy",
        _ => key.ToLowerInvariant(),
    };

    private static string CategoryOf(string key) =>
        EmotionKeys.Contains(key) ? "emotion"
        : MouthKeys.Contains(key) ? "mouth"
        : BlinkKeys.Contains(key) ? "blink"
        : LookKeys.Contains(key) ? "look"
        : "custom";

    // ───────── 弹簧骨骼 ─────────

    private static List<VrmSpringGroup> ParseSprings(JsonElement root, List<VrmNode> nodes, Dictionary<string, int> humanoid)
    {
        var groups = new List<VrmSpringGroup>();
        if (!root.TryGetProperty("extensions", out var exts))
            return groups;

        if (exts.TryGetProperty("VRM", out var v0) && v0.TryGetProperty("secondaryAnimation", out var sec))
        {
            // 顶层 colliderGroups 定义:{node, colliders:[{offset{xyz}, radius}]};boneGroup 按索引引用
            var colliderDefs = new List<(int node, float radius, Vector3 offset)>();
            if (sec.TryGetProperty("colliderGroups", out var defGroups))
            {
                foreach (var cg in defGroups.EnumerateArray())
                {
                    var colliderNode = cg.GetProperty("node").GetInt32();
                    if (!cg.TryGetProperty("colliders", out var colliderList))
                        continue;
                    foreach (var col in colliderList.EnumerateArray())
                        colliderDefs.Add((colliderNode,
                            col.TryGetProperty("radius", out var r) ? r.GetSingle() : 0.08f,
                            col.TryGetProperty("offset", out var o) ? ParseVec3(o) : Vector3.Zero));
                }
            }
            if (!sec.TryGetProperty("boneGroups", out var bgs))
                return groups;
            foreach (var g in bgs.EnumerateArray())
            {
                var group = new VrmSpringGroup
                {
                    Center = g.TryGetProperty("center", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1,
                };
                float stiffness = g.TryGetProperty("stiffiness", out var st) ? st.GetSingle() : 1f;
                float drag = g.TryGetProperty("dragForce", out var df) ? df.GetSingle() : 0.4f;
                float gravityPower = g.TryGetProperty("gravityPower", out var gp) ? gp.GetSingle() : 0f;
                var gdir = new Vector3(0f, -1f, 0f);
                if (g.TryGetProperty("gravityDir", out var gd))
                    gdir = ParseVec3(gd);
                float hitRadius = g.TryGetProperty("hitRadius", out var hr) ? hr.GetSingle() : 0.04f;

                if (g.TryGetProperty("bones", out var bones))
                {
                    foreach (var head in bones.EnumerateArray())
                        AddChain(group, head.GetInt32(), nodes, stiffness, drag, gravityPower, gdir, hitRadius, -1);
                }
                if (g.TryGetProperty("colliderGroups", out var cgs))
                {
                    // boneGroup.colliderGroups 是【索引数组】,引用 secondaryAnimation.colliderGroups
                    foreach (var idx in cgs.EnumerateArray())
                        if (idx.ValueKind == JsonValueKind.Number && idx.GetInt32() < colliderDefs.Count)
                        {
                            var def = colliderDefs[idx.GetInt32()];
                            group.Colliders.Add(new VrmSpringCollider { Node = def.node, Radius = def.radius, Offset = def.offset });
                        }
                }
                groups.Add(group);
            }
        }

        if (exts.TryGetProperty("VRMC_springBone", out var v1))
        {
            // 顶层 colliders[] 是定义(sphere 形状),spring.colliders 按索引引用
            var colliderDefs = new List<VrmSpringCollider>();
            foreach (var c in v1.TryGetProperty("colliders", out var cs) ? cs.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                colliderDefs.Add(new VrmSpringCollider
                {
                    Node = c.TryGetProperty("node", out var n) ? n.GetInt32() : humanoid.TryGetValue("head", out var head) ? head : -1,
                    Radius = c.TryGetProperty("shape", out var shape) && shape.TryGetProperty("sphere", out var sphere)
                        ? sphere.TryGetProperty("radius", out var r) ? r.GetSingle() : 0.08f
                        : 0.08f,
                    Offset = shape.TryGetProperty("sphere", out var sph) && sph.TryGetProperty("offset", out var off)
                        ? new Vector3(off[0].GetSingle(), off[1].GetSingle(), off[2].GetSingle())
                        : Vector3.Zero,
                });
            }
            foreach (var spring in v1.TryGetProperty("springs", out var sp) ? sp.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var group = new VrmSpringGroup
                {
                    Center = spring.TryGetProperty("center", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1,
                };
                foreach (var j in spring.TryGetProperty("joints", out var js) ? js.EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    group.Joints.Add(new VrmSpringJoint
                    {
                        Node = j.GetProperty("node").GetInt32(),
                        Stiffness = j.TryGetProperty("stiffness", out var st) ? st.GetSingle() : 1f,
                        DragForce = j.TryGetProperty("dragForce", out var df) ? df.GetSingle() : 0.4f,
                        GravityPower = j.TryGetProperty("gravityPower", out var gp) ? gp.GetSingle() : 0f,
                        GravityDir = j.TryGetProperty("gravityDir", out var gd) ? ParseVec3(gd) : new Vector3(0f, -1f, 0f),
                        HitRadius = j.TryGetProperty("hitRadius", out var hr) ? hr.GetSingle() : 0.05f,
                    });
                }
                foreach (var idx in spring.TryGetProperty("colliders", out var sci) ? sci.EnumerateArray() : Enumerable.Empty<JsonElement>())
                    if (idx.ValueKind == JsonValueKind.Number && idx.GetInt32() < colliderDefs.Count)
                        group.Colliders.Add(colliderDefs[idx.GetInt32()]);
                groups.Add(group);
            }
        }

        // 补全链:每个 joint 的 tail(链中下一节点)与其父(链中上一 joint,否则用节点自己的父)
        foreach (var g in groups)
        {
            var byNode = g.Joints.ToDictionary(j => j.Node);
            foreach (var j in g.Joints)
            {
                var node = nodes[j.Node];
                j.TailNode = node.Children.FirstOrDefault(c => byNode.ContainsKey(c), -1);
            }
        }
        return groups;
    }

    /// <summary>VRM0:boneGroup.bones 是链头,头及其带 mesh 权重的子孙都是弹簧骨骼,按深度顺序加入。</summary>
    private static void AddChain(VrmSpringGroup group, int head, List<VrmNode> nodes,
        float stiffness, float drag, float gravityPower, Vector3 gravityDir, float hitRadius, int _)
    {
        // 广度优先保证父先于子
        var queue = new Queue<int>();
        queue.Enqueue(head);
        while (queue.Count > 0)
        {
            var idx = queue.Dequeue();
            group.Joints.Add(new VrmSpringJoint
            {
                Node = idx,
                Stiffness = stiffness,
                DragForce = drag,
                GravityPower = gravityPower,
                GravityDir = gravityDir,
                HitRadius = hitRadius,
            });
            foreach (var c in nodes[idx].Children)
                queue.Enqueue(c);
        }
    }
}
