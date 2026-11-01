using System.IO;
using System.Text.Json;
using OpenTK.Mathematics;

namespace APet.Renderer.Vrm;

/// <summary>插值方式:CUBICSPLINE 退化为线性(本库的 VRMA 采样器)。</summary>
public enum VrmaInterp { Linear, Step }

/// <summary>VRMA 里某根骨骼的一条曲线通道。Times 与各值数组等长、按时间升序。</summary>
public sealed class VrmAnimChannel
{
    public required string Bone;            // VRM1 humanoid 名(与 VrmModel.HumanBones 键一致,大小写不敏感匹配)
    public bool IsRotation;                 // true=旋转;false=平移
    public float[] Times = Array.Empty<float>();
    /// <summary>旋转曲线(仅 IsRotation):解析时已重定向为「最近 humanoid 父骨帧下的相对物理旋转」
    /// (D = R_父⁻¹·R_b,R = 相对 T-pose 的世界物理旋转)。播放侧再套模型自身 bind(L = MB_父⁻¹·D·MB_b)。
    /// 这样 VRMA 骨架的轴约定差异(hips/腿/肩的大角度 bind)与模型骨骼(如拇指根部 100°+ bind)都被正确处理。</summary>
    public Quaternion[] Rotations = Array.Empty<Quaternion>();
    public Vector3[] Translations = Array.Empty<Vector3>();
    public VrmaInterp Interp = VrmaInterp.Linear;
}

/// <summary>一条已解析的 VRMA 动画(取文件里的第 0 个 animation)。旋转曲线已重定向为父骨帧下的
/// 相对物理旋转;播放时由 VrmStage 套上模型自身 bind 世界帧后作为 humanoid 骨骼的局部旋转替换。</summary>
public sealed class VrmAnimation
{
    public required string Name;
    public float Duration;
    public required List<VrmAnimChannel> Channels;
    /// <summary>VRMA 骨架 hips 的 bind 平移(骨架臀高,米)。播放侧按 模型臀高/此值 缩放 hips 平移,避免人物下沉。</summary>
    public Vector3 HipsBindTranslation;
}

/// <summary>VRMA(.vrma,本质是带 VRMC_vrm_animation 扩展的 GLB)解析器。复用 GltfSource 原样读取。</summary>
public static class VrmAnimationLoader
{
    // 兼容 legacy(VRM0 muscle / HWA)命名的兜底别名 → VRM1 camelCase;VRM1 命名的 VRMA 用不到,仅健壮性。
    private static readonly Dictionary<string, string> BoneAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UpperLeftArm"] = "leftUpperArm", ["LowerLeftArm"] = "leftLowerArm",
        ["UpperRightArm"] = "rightUpperArm", ["LowerRightArm"] = "rightLowerArm",
        ["LeftElbow"] = "leftLowerArm", ["RightElbow"] = "rightLowerArm",
        ["LeftWrist"] = "leftHand", ["RightWrist"] = "rightHand",
        ["LeftHand"] = "leftHand", ["RightHand"] = "rightHand",
        ["UpperLeftLeg"] = "leftUpperLeg", ["LowerLeftLeg"] = "leftLowerLeg",
        ["UpperRightLeg"] = "rightUpperLeg", ["LowerRightLeg"] = "rightLowerLeg",
        ["LeftFoot"] = "leftFoot", ["RightFoot"] = "rightFoot",
        ["Body"] = "hips", ["Head"] = "head",
    };

    private static string NormalizeBone(string name) =>
        BoneAlias.TryGetValue(name, out var m) ? m : name;

    private static Quaternion ReadNodeRotation(JsonElement node) =>
        node.TryGetProperty("rotation", out var rq) && rq.ValueKind == JsonValueKind.Array && rq.GetArrayLength() == 4
            ? new Quaternion(rq[0].GetSingle(), rq[1].GetSingle(), rq[2].GetSingle(), rq[3].GetSingle())
            : Quaternion.Identity;

    private static Vector3 ReadNodeTranslation(JsonElement node) =>
        node.TryGetProperty("translation", out var tq) && tq.ValueKind == JsonValueKind.Array && tq.GetArrayLength() == 3
            ? new Vector3(tq[0].GetSingle(), tq[1].GetSingle(), tq[2].GetSingle())
            : Vector3.Zero;

    /// <summary>VRMA 骨架上某节点的原始旋转通道(含非 humanoid 的 twist 等,重定向中间链需要)。</summary>
    private readonly record struct RawRot(float[] Times, Quaternion[] Values, VrmaInterp Interp);

    /// <summary>按时间 t 采样原始旋转通道(线性 Slerp / STEP)。</summary>
    private static Quaternion SampleRaw(in RawRot ch, float t)
    {
        int i = 0;
        while (i < ch.Times.Length - 1 && ch.Times[i + 1] <= t)
            i++;
        if (ch.Interp == VrmaInterp.Step || i >= ch.Times.Length - 1)
            return ch.Values[i];
        float t0 = ch.Times[i], t1 = ch.Times[i + 1];
        float f = t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0f, 1f) : 0f;
        return Quaternion.Slerp(ch.Values[i], ch.Values[i + 1], f);
    }

    /// <summary>沿节点树计算每个节点的 bind 世界旋转(world = 父 world × 本节点 bind)与父节点表。</summary>
    private static (Quaternion[] World, int[] Parent) BuildBindFrames(JsonElement nodesArr, int nodeCount)
    {
        var world = new Quaternion[nodeCount];
        var parent = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            world[i] = Quaternion.Identity;
            parent[i] = -1;
        }
        if (nodeCount == 0)
            return (world, parent);

        for (int i = 0; i < nodeCount; i++)
            if (nodesArr[i].TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray())
                {
                    int ci = c.GetInt32();
                    if (ci >= 0 && ci < nodeCount)
                        parent[ci] = i;
                }

        var stack = new Stack<int>();
        for (int i = 0; i < nodeCount; i++)
            if (parent[i] < 0)
            {
                world[i] = ReadNodeRotation(nodesArr[i]);
                stack.Push(i);
            }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (!nodesArr[i].TryGetProperty("children", out var ch) || ch.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var c in ch.EnumerateArray())
            {
                int ci = c.GetInt32();
                if (ci < 0 || ci >= nodeCount || parent[ci] != i)
                    continue;
                world[ci] = world[i] * ReadNodeRotation(nodesArr[ci]);
                stack.Push(ci);
            }
        }
        return (world, parent);
    }

    /// <summary>解析 .vrma;失败/不支持(无动画或无 humanoid 映射)返回 null。</summary>
    public static VrmAnimation? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var src = GltfSource.Load(path);
            var root = src.Root;
            if (!root.TryGetProperty("extensions", out var exts))
                return null;

            // VRMA 节点号 → humanoid 骨骼名
            var node2bone = new Dictionary<int, string>();
            if (exts.TryGetProperty("VRMC_vrm_animation", out var va)
                && va.TryGetProperty("humanoid", out var hu)
                && hu.TryGetProperty("humanBones", out var hb)
                && hb.ValueKind == JsonValueKind.Object)
            {
                foreach (var b in hb.EnumerateObject())
                    if (b.Value.TryGetProperty("node", out var nd) && nd.ValueKind == JsonValueKind.Number)
                        node2bone[nd.GetInt32()] = NormalizeBone(b.Name);
            }
            else if (exts.TryGetProperty("VRM", out var v0)
                && v0.TryGetProperty("humanoid", out var hu0)
                && hu0.TryGetProperty("humanBones", out var arr0)
                && arr0.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr0.EnumerateArray())
                    if (b.TryGetProperty("bone", out var bn) && b.TryGetProperty("node", out var nd)
                        && bn.ValueKind == JsonValueKind.String && nd.ValueKind == JsonValueKind.Number)
                        node2bone[nd.GetInt32()] = NormalizeBone(bn.GetString()!);
            }
            if (node2bone.Count == 0)
                return null;

            if (!root.TryGetProperty("animations", out var anims) || anims.GetArrayLength() == 0)
                return null;
            var a = anims[0];
            if (!a.TryGetProperty("channels", out var channels) || !a.TryGetProperty("samplers", out var samplers))
                return null;

            root.TryGetProperty("nodes", out var nodesArr);
            int nodeCount = nodesArr.ValueKind == JsonValueKind.Array ? nodesArr.GetArrayLength() : 0;
            var (bindWorld, parentNode) = BuildBindFrames(nodesArr, nodeCount);

            // hips 节点 bind 平移(VRMA 骨架臀高,供播放侧按模型比例缩放根位移)
            var hipsBind = Vector3.Zero;
            foreach (var kv in node2bone)
                if (kv.Value.Equals("hips", StringComparison.OrdinalIgnoreCase) && kv.Key >= 0 && kv.Key < nodeCount)
                {
                    hipsBind = ReadNodeTranslation(nodesArr[kv.Key]);
                    break;
                }

            // 第一遍:原样解析全部旋转通道(humanoid + twist,重定向中间链要用)、humanoid 平移通道
            var rawRot = new Dictionary<int, RawRot>();
            var list = new List<VrmAnimChannel>();
            float duration = 0f;
            foreach (var ch in channels.EnumerateArray())
            {
                if (!ch.TryGetProperty("target", out var target))
                    continue;
                int node = target.GetProperty("node").GetInt32();
                string pth = target.GetProperty("path").GetString() ?? "";
                bool isRot = pth == "rotation";
                bool isTrans = pth == "translation";
                if (!isRot && !isTrans)
                    continue; // 忽略 weights(表情)等其它路径

                int sIdx = ch.GetProperty("sampler").GetInt32();
                var sampler = samplers[sIdx];
                var times = src.ReadFloats(sampler.GetProperty("input").GetInt32()); // SCALAR
                var outFlat = src.ReadFloats(sampler.GetProperty("output").GetInt32());
                int n = times.Length;
                if (n == 0)
                    continue;
                var interp = sampler.TryGetProperty("interpolation", out var ip) && ip.GetString() == "STEP"
                    ? VrmaInterp.Step : VrmaInterp.Linear;

                if (isRot)
                {
                    var r = new Quaternion[n];
                    for (int i = 0; i < n && (i * 4 + 3) < outFlat.Length; i++)
                        r[i] = new Quaternion(outFlat[i * 4], outFlat[i * 4 + 1], outFlat[i * 4 + 2], outFlat[i * 4 + 3]);
                    rawRot[node] = new RawRot(times, r, interp);
                    // humanoid 骨建通道(值稍后重定向);非 humanoid(twist)只进 rawRot 供中间链采样
                    if (node2bone.TryGetValue(node, out var bone))
                        list.Add(new VrmAnimChannel { Bone = bone, IsRotation = true, Times = times, Rotations = r, Interp = interp });
                }
                else
                {
                    if (!node2bone.TryGetValue(node, out var bone))
                        continue; // 平移仅 humanoid(实际标准里只有 hips)有意义
                    var t = new Vector3[n];
                    for (int i = 0; i < n && (i * 3 + 2) < outFlat.Length; i++)
                        t[i] = new Vector3(outFlat[i * 3], outFlat[i * 3 + 1], outFlat[i * 3 + 2]);
                    list.Add(new VrmAnimChannel { Bone = bone, IsRotation = false, Times = times, Translations = t, Interp = interp });
                }
                duration = MathF.Max(duration, times[n - 1]);
            }
            if (list.Count == 0)
                return null;

            // 第二遍:把每个 humanoid 旋转通道重定向为「最近 humanoid 父骨帧下的相对物理旋转」D。
            // 对骨架层级里 b 与父骨 p 之间的中间节点(twist 等):其旋转也参与链积,随动画或 bind 取值。
            foreach (var cc in list)
            {
                if (!cc.IsRotation)
                    continue;
                int bNode = -1;
                foreach (var kv in node2bone)
                    if (kv.Value.Equals(cc.Bone, StringComparison.OrdinalIgnoreCase))
                    {
                        bNode = kv.Key;
                        break;
                    }
                if (bNode < 0 || bNode >= nodeCount)
                    continue;

                // 最近 humanoid 祖先 p 与中间节点链 mid=[直接父…p 的子](自 b 向上收集,不含 p)
                var mid = new List<int>();
                int pNode = -1;
                for (int cur = parentNode[bNode]; cur >= 0; cur = parentNode[cur])
                {
                    if (node2bone.ContainsKey(cur))
                    {
                        pNode = cur;
                        break;
                    }
                    mid.Add(cur);
                }

                Quaternion wbBInv = Quaternion.Invert(bindWorld[bNode]);
                Quaternion wbP = pNode >= 0 ? bindWorld[pNode] : Quaternion.Identity;
                var rel = new Quaternion[cc.Times.Length];
                for (int k = 0; k < cc.Times.Length; k++)
                {
                    float t = cc.Times[k];
                    // WA_p⁻¹·WA_b = X_cm·…·X_c1·anim_b(X_ci 为中间节点 t 时刻局部旋转,离 b 越近越靠右)
                    Quaternion acc = cc.Rotations[k];
                    for (int m = 0; m < mid.Count; m++)
                    {
                        int ci = mid[m];
                        Quaternion x = rawRot.TryGetValue(ci, out var rc) ? SampleRaw(rc, t) : ReadNodeRotation(nodesArr[ci]);
                        acc = x * acc;
                    }
                    // D = WB_p · (WA_p⁻¹·WA_b) · WB_b⁻¹:父骨帧下的物理旋转增量
                    rel[k] = wbP * acc * wbBInv;
                }
                cc.Rotations = rel;
            }

            return new VrmAnimation
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Duration = duration,
                Channels = list,
                HipsBindTranslation = hipsBind,
            };
        }
        catch
        {
            return null;
        }
    }
}
