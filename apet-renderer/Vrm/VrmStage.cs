using System.Diagnostics;
using OpenTK.Mathematics;

namespace APet.Renderer.Vrm;

/// <summary>
/// VRM 运行时:骨骼世界矩阵、表情权重、morph + CPU 蒙皮、弹簧骨骼物理、内建待机
/// (呼吸 / 环视 / 眨眼),并把宿主可编程的控制面(骨骼/表情/视线)完整暴露。
/// 所有接口均为线程内调用(与悬浮窗同线程);GetSkinnedVertices 供渲染器取最新蒙皮结果。
/// </summary>
public sealed class VrmStage
{
    public VrmModel Model { get; }

    private readonly Dictionary<string, Quaternion> _poseOverrides = new(StringComparer.OrdinalIgnoreCase);  // 宿主人骨覆盖
    private readonly Dictionary<string, Quaternion> _idleRotations = new(StringComparer.OrdinalIgnoreCase);  // 内建随机 idle(按 humanoid 名)
    private readonly Dictionary<string, float> _weights = new(StringComparer.OrdinalIgnoreCase);      // 当前
    private readonly Dictionary<string, float> _weightTargets = new(StringComparer.OrdinalIgnoreCase); // 目标
    private readonly Random _random = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _lastTime;

    // ───────── VRMA 动画播放 ─────────
    private VrmAnimation? _animation;
    private float _animTime;
    private bool _animLoop;
    private bool _animActive;
    /// <summary>动画姿势权重 0~1:进入时淡入、结束前淡出;1 = 完全由动画驱动。</summary>
    private float _animWeight;
    private bool _animFadingOut;
    /// <summary>动作与待机互切的淡入/淡出时长(秒)。</summary>
    private const float AnimFadeSeconds = 0.25f;
    private readonly Dictionary<string, Quaternion> _animationRotations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> _animationTranslations = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>模型侧重定向表:骨名 → (Pre=MB_父⁻¹, Post=MB_自身)。MB=模型 bind 世界旋转。L = Pre·D·Post。</summary>
    private readonly Dictionary<string, (Quaternion Pre, Quaternion Post)> _animRetarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Quaternion> _bindWorldRot = new(StringComparer.OrdinalIgnoreCase); // 人骨 bind 世界旋转(待机拇指判据用)
    private readonly Dictionary<string, Vector3> _bindWorldPos = new(StringComparer.OrdinalIgnoreCase);    // 人骨 bind 世界位置
    private readonly Dictionary<string, (Quaternion Proximal, Quaternion Metacarpal)> _thumbCurlDelta = new(StringComparer.OrdinalIgnoreCase); // 待机拇指卷曲增量("left"/"right")
    private float _modelHipsBindY;      // 模型 hips 的 bind 世界高度
    private float _animTransScale = 1f; // 本条动画 hips 平移缩放比(模型臀高 / VRMA 骨架臀高)
    /// <summary>VRMA 世界系朝向修正:VRMA 骨架遵循 VRM1/glTF(脸朝 +Z),模型脸朝 -Z(VRM0 常见)
    /// 时世界系增量需绕 Y 共轭,否则摆臂/转头/移动方向全部反向。脸朝 +Z 的模型为恒等。</summary>
    private Quaternion _animWorldFix = Quaternion.Identity;
    /// <summary>非循环 VRMA 动作播完(含淡出)时触发(参数=动作名),供宿主回待机。</summary>
    public event Action<string>? AnimationCompleted;

    // 待机内建
    public bool IdleMotionEnabled { get; set; } = true;
    public bool SpringBoneEnabled { get; set; } = true;
    public bool SpringCollidersEnabled { get; set; } = true;
    private float _nextBlink;
    private float _blinkPhase = -1f;
    private float _nextGazeSwitch;
    private Vector2 _gazeTarget;      // yaw / pitch(弧度)
    private Vector2 _gazeCurrent;

    // 程序化随机 idle:各通道当前值/目标值(随机切换后平滑趋近),让挂机时身体自然活动
    private readonly Dictionary<string, (float cur, float tgt)> _idlePose = new(StringComparer.Ordinal);
    private float _idleNextSwitch;
    private float _microPhase;        // 微动噪声相位
    private float _microFreq;         // 微动噪声频率
    private float _armSwayPhase;      // 手臂摆动相位

    // 视线控制(宿主):弧度;为 null 时走内建环视
    private Vector2? _lookAtOverride;

    /// <summary>模型朝向修正(弧度)。构造时按眼骨自动检测:脸朝 +Z 的模型转 π 面向 -Z 侧相机,
    /// 脸朝 -Z 的保持 0;无眼骨时按世代兜底(VRM0→0, VRM1→π)。可手动覆盖。</summary>
    public float ModelYawOffset { get; set; }

    public VrmStage(VrmModel model)
    {
        Model = model;
        ModelYawOffset = 0f;
        UpdateWorlds(); // 先按 yaw=0 算一次世界,供朝向检测
        ModelYawOffset = DetectYawOffset();
        UpdateWorlds();
        UpdateSpringBones(0f, true);
        BuildAnimRetarget();
        // 每个蒙皮 primitive 预分配输出缓冲(pos3 + normal3 + uv2)
        foreach (var p in Model.Primitives)
            p.Skinned = new float[p.BasePositions.Length * 8];
        SkinAll();
    }

    /// <summary>构建模型侧重定向表:沿模型节点层级累积各 humanoid 骨的 bind 世界旋转(MB)与 hips bind 高度;
    /// 播放时 L = MB_父⁻¹ · D · MB_自身,把 VRMA 的物理旋转增量正确套到模型自身 bind 上
    /// (否则拇指根部这类 100°+ bind 的骨骼会被绝对替换成错误朝向)。</summary>
    private void BuildAnimRetarget()
    {
        _animRetarget.Clear();
        _modelHipsBindY = 0f;
        if (Model.HumanBones.Count == 0)
            return;
        var node2bone = new Dictionary<int, string>();
        foreach (var kv in Model.HumanBones)
            node2bone[kv.Value] = kv.Key;

        var worldRot = new Dictionary<int, Quaternion>();
        var worldPos = new Dictionary<int, Vector3>();
        var stack = new Stack<int>();
        for (int i = 0; i < Model.Nodes.Count; i++)
            if (Model.Nodes[i].Parent < 0)
            {
                var nd = Model.Nodes[i];
                worldRot[i] = nd.LocalRotation;
                worldPos[i] = nd.LocalTranslation;
                stack.Push(i);
            }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            foreach (var c in Model.Nodes[i].Children)
            {
                var nd = Model.Nodes[c];
                worldRot[c] = worldRot[i] * nd.LocalRotation;
                worldPos[c] = worldPos[i] + Rotate(worldRot[i], nd.LocalTranslation);
                stack.Push(c);
            }
        }
        foreach (var kv in Model.HumanBones)
        {
            // 父骨 = 节点层级向上最近的 humanoid 骨(跨过 twist 等非 humanoid 节点)
            string? pb = null;
            for (int cur = Model.Nodes[kv.Value].Parent; cur >= 0 && pb is null; cur = Model.Nodes[cur].Parent)
                if (node2bone.TryGetValue(cur, out var b0))
                    pb = b0;
            var pre = pb is null ? Quaternion.Identity : Quaternion.Invert(worldRot[Model.HumanBones[pb]]);
            _animRetarget[kv.Key] = (pre, worldRot[kv.Value]);
            _bindWorldRot[kv.Key] = worldRot[kv.Value];
            _bindWorldPos[kv.Key] = worldPos[kv.Value];
        }
        if (Model.HumanBones.TryGetValue("hips", out var hipsIdx) && worldPos.TryGetValue(hipsIdx, out var hp))
            _modelHipsBindY = hp.Y;
        ComputeThumbCurlDeltas();
    }

    /// <summary>预计算待机拇指收拢增量(根部 + 近端两个关节)。拇指骨骼的局部轴朝向因模型而异
    /// (部分 VRM0 与手臂镜像假设不同构),固定用局部 Z 会把拇指扭向背离食指的方向,并非「收拢」。
    /// 目标几何:拇指各段方向与「实际待机姿态下(四指已卷曲)的食指方向」平行 —— 参考必须取卷曲
    /// 后的食指,取静止位置会因食指实际内卷而让拇指向外甩。任何骨架拓扑/轴约定下都收敛到同一效果。</summary>
    private void ComputeThumbCurlDeltas()
    {
        foreach (var side in new[] { "left", "right" })
        {
            // 兜底 = 旧约定(仅近端,局部 Z × 四指镜像符号):骨骼缺失或几何退化时行为与既往完全一致
            float fallbackSign = (side == "left" ? -1f : 1f) * ArmMirrorFactor(side + "UpperArm", side + "LowerArm");
            _thumbCurlDelta[side] = (QuaternionFromAxis(Vector3.UnitZ, ThumbCurl * fallbackSign), Quaternion.Identity);
            if (!Model.HumanBones.TryGetValue(side + "ThumbProximal", out var tp)
                || !Model.HumanBones.TryGetValue(side + "ThumbDistal", out var td)
                || !Model.HumanBones.TryGetValue(side + "IndexProximal", out var ip)
                || !Model.HumanBones.TryGetValue(side + "IndexIntermediate", out var im))
                continue;
            if (!_bindWorldRot.TryGetValue(side + "ThumbProximal", out var wTp)
                || !_bindWorldRot.TryGetValue(side + "IndexProximal", out var wIp))
                continue;
            // 实际待机的食指方向(四指卷曲是常量增量,食指近节卷曲带动中节方向)
            var indexDir = Rotate(wIp * QuaternionFromAxis(Vector3.UnitZ, FingerCurlProximal * (side == "left" ? -1f : 1f) * ArmMirrorFactor(side + "UpperArm", side + "LowerArm")),
                Model.Nodes[im].LocalTranslation);
            if (indexDir.LengthSquared < 1e-10f)
                continue;
            indexDir = Vector3.Normalize(indexDir);
            var tipPos = _bindWorldPos.TryGetValue(side + "ThumbDistal", out var tp3) ? tp3 : Vector3.Zero;

            // 段方向 → 食指方向 的最优轴(世界系 → 关节局部系)
            Quaternion TuckDelta(string boneKey, Vector3 segFrom, Vector3 segTo, float ratio)
            {
                var axisW = Vector3.Cross(segTo - segFrom, indexDir);
                if (axisW.LengthSquared < 1e-10f || !_bindWorldRot.TryGetValue(boneKey, out var w))
                    return Quaternion.Identity;
                var axisL = Vector3.Normalize(Rotate(Quaternion.Invert(w), axisW));
                return QuaternionFromAxis(axisL, ThumbCurl * ratio);
            }
            var pivotPos = _bindWorldPos.TryGetValue(side + "ThumbProximal", out var pv) ? pv : Vector3.Zero;
            var proximal = TuckDelta(side + "ThumbProximal", pivotPos, tipPos, ThumbProxTuckRatio);
            var metacarpal = Quaternion.Identity;
            if (Model.HumanBones.ContainsKey(side + "ThumbMetacarpal")
                && _bindWorldPos.TryGetValue(side + "ThumbMetacarpal", out var basePos))
                metacarpal = TuckDelta(side + "ThumbMetacarpal", basePos, pivotPos != Vector3.Zero ? pivotPos : tipPos, ThumbRootTuckRatio);
            _thumbCurlDelta[side] = (proximal, metacarpal);
        }
    }

    // ───────── 数学辅助(OpenTK 缺口的补齐) ─────────

    // ───────── 数学约定:全部按 glTF 的列向量语义手写(v' = M·v,平移在第四列),
    // 不依赖 OpenTK 的矩阵乘法/变换(其为行向量约定,混用会顶点飞散) ─────────

    internal static Matrix4 MatMul(Matrix4 a, Matrix4 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
        a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
        a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
        a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
        a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
        a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
        a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
        a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
        a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
        a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,
        a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
        a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
        a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
        a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44);

    internal static Vector3 XformPoint(Matrix4 m, Vector3 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34);

    internal static Vector4 XformPoint4(Matrix4 m, Vector3 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34,
        m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44);

    internal static Vector3 XformNormal(Matrix4 m, Vector3 v) => new(
        m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
        m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
        m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

    /// <summary>提取平移分量。本库矩阵为列向量约定(平移在第 4 列 M14/M24/M34),
    /// OpenTK 的 ExtractTranslation 取第 4 行,对本库矩阵恒返回 (0,0,0),不可用。</summary>
    internal static Vector3 ExtractTranslation(Matrix4 m) => new(m.M14, m.M24, m.M34);

    internal static Matrix4 Compose(Vector3 t, Quaternion r, Vector3 s)
    {
        // 标准四元数 → 旋转矩阵(列向量数学)
        float x2 = r.X + r.X, y2 = r.Y + r.Y, z2 = r.Z + r.Z;
        float xx = r.X * x2, xy = r.X * y2, xz = r.X * z2;
        float yy = r.Y * y2, yz = r.Y * z2, zz = r.Z * z2;
        float wx = r.W * x2, wy = r.W * y2, wz = r.W * z2;
        return new Matrix4(
            (1 - (yy + zz)) * s.X, (xy - wz) * s.Y, (xz + wy) * s.Z, t.X,
            (xy + wz) * s.X, (1 - (xx + zz)) * s.Y, (yz - wx) * s.Z, t.Y,
            (xz - wy) * s.X, (yz + wx) * s.Y, (1 - (xx + yy)) * s.Z, t.Z,
            0, 0, 0, 1);
    }

    internal static Vector3 ExtractScale(Matrix4 m) =>
        new(new Vector3(m.M11, m.M12, m.M13).Length, new Vector3(m.M21, m.M22, m.M23).Length, new Vector3(m.M31, m.M32, m.M33).Length);

    /// <summary>正交旋转矩阵 → 四元数(Shepperd 法,数值稳定)。</summary>
    internal static Quaternion ToQuaternion(Matrix4 m)
    {
        float trace = m.M11 + m.M22 + m.M33;
        Quaternion q;
        if (trace > 0f)
        {
            var s = MathF.Sqrt(trace + 1f) * 2f;
            q = new Quaternion((m.M32 - m.M23) / s, (m.M13 - m.M31) / s, (m.M21 - m.M12) / s, 0.25f * s);
        }
        else if (m.M11 > m.M22 && m.M11 > m.M33)
        {
            var s = MathF.Sqrt(1f + m.M11 - m.M22 - m.M33) * 2f;
            q = new Quaternion(0.25f * s, (m.M12 + m.M21) / s, (m.M13 + m.M31) / s, (m.M32 - m.M23) / s);
        }
        else if (m.M22 > m.M33)
        {
            var s = MathF.Sqrt(1f + m.M22 - m.M11 - m.M33) * 2f;
            q = new Quaternion((m.M12 + m.M21) / s, 0.25f * s, (m.M23 + m.M32) / s, (m.M13 - m.M31) / s);
        }
        else
        {
            var s = MathF.Sqrt(1f + m.M33 - m.M11 - m.M22) * 2f;
            q = new Quaternion((m.M13 + m.M31) / s, (m.M23 + m.M32) / s, (m.M21 - m.M12) / s, 0.25f * s);
        }
        return q;
    }

    internal static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        var dot = Vector3.Dot(from, to);
        if (axis.LengthSquared < 1e-10f)
            axis = dot < 0f ? Vector3.UnitX : Vector3.UnitY; // 共线:任取正交轴
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return Quaternion.FromAxisAngle(Vector3.Normalize(axis), angle);
    }

    // ───────── 宿主控制接口 ─────────

    /// <summary>设置/清除表情权重目标(0..1),随帧平滑过渡。key 不存在时忽略。</summary>
    public void SetExpression(string key, float weight)
    {
        if (!Model.Expressions.ContainsKey(key))
            return;
        weight = Math.Clamp(weight, 0f, 1f);
        _weightTargets[key] = weight;
    }

    /// <summary>清除全部表情(权重目标归零)。</summary>
    public void ResetExpressions()
    {
        foreach (var k in _weightTargets.Keys.ToList())
            _weightTargets[k] = 0f;
    }

    public IReadOnlyCollection<string> ExpressionKeys => Model.Expressions.Keys;

    /// <summary>按 humanoid 名覆盖某骨骼的局部旋转(如 "head"、"leftUpperArm");传 null 清除。</summary>
    public void SetBoneLocalRotation(string humanBone, Quaternion? rotation)
    {
        if (rotation is null)
            _poseOverrides.Remove(humanBone);
        else if (Model.HumanBones.TryGetValue(humanBone, out var node))
            _poseOverrides[humanBone] = rotation.Value;
    }

    /// <summary>清除全部骨骼覆盖(回到文件默认姿势)。</summary>
    public void ResetPose() => _poseOverrides.Clear();

    /// <summary>设置视线(俯仰/偏航,弧度);传 null 恢复内建随机环视。</summary>
    public void SetLookAt(float? yaw, float? pitch)
    {
        if (yaw is null || pitch is null)
            _lookAtOverride = null;
        else
            _lookAtOverride = new Vector2(yaw.Value, pitch.Value);
    }

    /// <summary>播放一条 VRMA 动画(替换当前)。进入时从当前待机姿势淡入(AnimFadeSeconds);
    /// 非循环播完先淡出回待机、再触发 AnimationCompleted。loop=true 则循环不停。</summary>
    public void PlayAnimation(VrmAnimation clip, bool loop)
    {
        _animation = clip;
        _animTime = 0f;
        _animLoop = loop;
        _animActive = true;
        _animFadingOut = false;
        // 保留 _animWeight:从待机进入时为 0(淡入);动作中途切换保持 1(立即满速,与既有行为一致)
        // hips 平移按「模型臀高 / VRMA 骨架臀高」缩放:VRMA 骨架腿长普遍偏短,原样套用会让人物下沉
        _animTransScale = _modelHipsBindY > 1e-4f && clip.HipsBindTranslation.Y > 1e-4f
            ? _modelHipsBindY / clip.HipsBindTranslation.Y
            : 1f;
        // 朝向修正:ModelYawOffset 语义为 π=脸朝 +Z、0=脸朝 -Z;VRMA 标准朝向是脸朝 +Z。
        // v1(π)差值 0 → 恒等;v0(0)差 -π → 世界系增量绕 Y 转 180°。
        float yawDiff = ModelYawOffset - MathF.PI;
        _animWorldFix = MathF.Abs(yawDiff) < 1e-4f
            ? Quaternion.Identity
            : QuaternionFromAxis(Vector3.UnitY, yawDiff);
    }

    /// <summary>停止当前 VRMA 动画,立即恢复内建待机(正常结束走淡出,不经过这里)。</summary>
    public void StopAnimation()
    {
        _animation = null;
        _animActive = false;
        _animWeight = 0f;
        _animFadingOut = false;
        _animationRotations.Clear();
        _animationTranslations.Clear();
    }

    /// <summary>当前是否有 VRMA 动画在播。</summary>
    public bool IsAnimationPlaying => _animActive;

    /// <summary>当前动画名(未在播为 null)。</summary>
    public string? CurrentAnimationName => _animation?.Name;

    // ───────── 每帧更新 ─────────

    public void Update()
    {
        var time = (float)_clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(time - _lastTime, 0.001f, 0.1f);
        _lastTime = time;

        UpdateIdle(time, dt);
        UpdateAnimation(dt);
        UpdateExpressionWeights(dt);
        UpdateWorlds();
        if (SpringBoneEnabled)
            UpdateSpringBones(dt, false);
        SkinAll();
    }

    private void UpdateIdle(float time, float dt)
    {
        // 待机关闭或动画满速播放期间:清掉 idle,避免与动画叠加;
        // 淡入/淡出混合期(w<1)继续更新 idle,作为姿势过渡的另一端点
        if (!IdleMotionEnabled || (_animActive && _animWeight >= 1f))
        {
            _idleRotations.Clear();
            return;
        }

        // 程序化随机 idle:随机切换目标姿态并平滑趋近(含呼吸/重心/微动噪声),让挂机时身体自然活动
        UpdateIdlePose(time, dt);

        // 眨眼:每隔 2~6 秒一次 0.18s 的权重脉冲
        if (_blinkPhase < 0f && time >= _nextBlink)
        {
            // 表情(闭眼)激活时跳过本次眨眼:眨眼 morph 与表情 morph 会同时位移眼皮顶点,
            // 叠加后眼皮移动过度导致穿模(例如在笑眼 ^^ 之上再眨一次)。此时不启动脉冲,
            // 仅推进计时,待表情权重回落后再自然眨眼。
            if (Model.Expressions.ContainsKey("blink") && !ExpressionSuppressesBlink())
                _blinkPhase = 0f;
            _nextBlink = time + 2f + _random.NextSingle() * 4f;
        }
        if (_blinkPhase >= 0f)
        {
            _blinkPhase += dt;
            // 80ms 上 + 20ms 平顶 + 80ms 下;平顶保证帧采样必然命中权重 1.0(完全闭上)
            var w = _blinkPhase < 0.08f ? _blinkPhase / 0.08f : (_blinkPhase <= 0.10f ? 1f : MathF.Max(0f, 1f - (_blinkPhase - 0.10f) / 0.08f));
            // 眨眼是快速瞬态:脉冲值直接写入当前权重(目标同步写,后续平滑成为 no-op)。
            // 若只写目标走指数平滑(时间常数 1/8s),0.18s 脉冲追不上,峰值仅约 0.44,眼皮只闭一半。
            _weightTargets["blink"] = w;
            _weights["blink"] = w;
            if (_blinkPhase > 0.18f)
            {
                _blinkPhase = -1f;
                _weightTargets["blink"] = 0f;
            }
        }
        // 视线:宿主接管或内建随机环视(目标缓动)
        if (_lookAtOverride is { } look)
            _gazeTarget = look;
        else if (time >= _nextGazeSwitch)
        {
            _gazeTarget = new Vector2((float)(_random.NextDouble() * 0.5 - 0.25), (float)(_random.NextDouble() * 0.3 - 0.15));
            _nextGazeSwitch = time + 2.5f + _random.NextSingle() * 4f;
        }
        _gazeCurrent = Vector2.Lerp(_gazeCurrent, _gazeTarget, Math.Clamp(dt * 3f, 0f, 1f));

        // 把头/胸/髋的 idle 姿态(含呼吸、环视、微动)合成到对应虚拟键,供 LocalRotationOf 应用
        ComposeIdleRotations(time);
    }

    /// <summary>推进 VRMA 时间并采样各骨骼曲线(套模型侧重定向后写入 _animationRotations/_animationTranslations)。
    /// 进入时权重淡入;非循环 clip 越界后冻结末帧并淡出回待机,权重归零才触发 AnimationCompleted ——
    /// 待机侧全程在更新,因此切换两端都平滑、结束瞬间不会闪 T-pose。循环 clip 持续取模。</summary>
    private void UpdateAnimation(float dt)
    {
        _animationRotations.Clear();
        _animationTranslations.Clear();
        if (!_animActive || _animation is null)
        {
            _animWeight = 0f;
            _animFadingOut = false;
            return;
        }

        if (!_animFadingOut)
        {
            if (_animWeight < 1f)
                _animWeight = MathF.Min(1f, _animWeight + dt / AnimFadeSeconds);
            _animTime += dt;
            if (_animTime >= _animation.Duration)
            {
                if (_animLoop && _animation.Duration > 0f)
                    _animTime %= _animation.Duration;
                else
                {
                    // 播完:冻结在末帧,淡出回待机。本帧 UpdateIdle 先于本方法执行、当时权重仍为 1
                    // (idle 被清空),先把权重标记为混合中并立即重建一次 idle,保证淡出端点是待机姿势
                    // 而非文件默认 T-pose(否则结束瞬间会闪一下 T-pose)。
                    _animFadingOut = true;
                    _animTime = _animation.Duration;
                    _animWeight = MathF.Min(_animWeight, 0.999f);
                    UpdateIdle(_lastTime, dt);
                }
            }
        }
        else
        {
            _animTime = _animation.Duration;
            _animWeight -= dt / AnimFadeSeconds;
            if (_animWeight <= 0f)
            {
                var done = _animation.Name;
                StopAnimation();
                AnimationCompleted?.Invoke(done);
                return;
            }
        }

        foreach (var ch in _animation.Channels)
        {
            if (ch.Times.Length == 0)
                continue;
            int i = 0;
            while (i < ch.Times.Length - 1 && ch.Times[i + 1] <= _animTime)
                i++;
            if (ch.Interp == VrmaInterp.Step || i >= ch.Times.Length - 1)
            {
                if (ch.IsRotation) ApplyAnimRotation(ch.Bone, ch.Rotations[i]);
                else _animationTranslations[ch.Bone] = Rotate(_animWorldFix, ch.Translations[i]) * _animTransScale;
                continue;
            }
            float t0 = ch.Times[i], t1 = ch.Times[i + 1];
            float f = t1 > t0 ? Math.Clamp((_animTime - t0) / (t1 - t0), 0f, 1f) : 0f;
            if (ch.IsRotation)
                ApplyAnimRotation(ch.Bone, Quaternion.Slerp(ch.Rotations[i], ch.Rotations[i + 1], f));
            else
                _animationTranslations[ch.Bone] = Rotate(_animWorldFix, Vector3.Lerp(ch.Translations[i], ch.Translations[i + 1], f)) * _animTransScale;
        }
    }

    /// <summary>把 VRMA 的相对物理旋转 D 套上模型自身 bind 世界帧:L = MB_父⁻¹ · D · MB_自身。
    /// 模型朝向与 VRMA 标准(+Z)不同时先绕 Y 共轭修正世界系;模型无该骨(非标准骨架)时原样使用。</summary>
    private void ApplyAnimRotation(string bone, Quaternion d)
    {
        if (_animWorldFix != Quaternion.Identity)
            d = _animWorldFix * d * Quaternion.Invert(_animWorldFix);
        _animationRotations[bone] = _animRetarget.TryGetValue(bone, out var rt)
            ? rt.Pre * d * rt.Post
            : d;
    }

    private static Quaternion QuaternionFromAxis(Vector3 axis, float angle) =>
        Quaternion.FromAxisAngle(axis, angle);

    /// <summary>按 glTF 列向量语义用四元数旋转向量(v' = q·v·q⁻¹),不依赖 OpenTK 的变换函数。</summary>
    private static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var t = 2f * Vector3.Cross(new Vector3(q.X, q.Y, q.Z), v);
        return v + q.W * t + Vector3.Cross(new Vector3(q.X, q.Y, q.Z), t);
    }

    /// <summary>上臂自然下垂的局部旋转。
    /// 手调轴角(ArmRestAxis*/ArmRestAngle)是按 VRM1 的手臂局部朝向(左 +X / 右 -X)求解的;
    /// 部分 VRM0 模型手臂朝向镜像(左 -X / 右 +X),直接套用会把手臂转到身体另一侧。
    /// 按"下臂相对上臂的局部平移 X 符号"检测实际朝向:与手调一致时原样返回(逐帧不变);
    /// 相反时以手调轴角反推出的下垂目标方向用 FromToRotation 重新求解,得到正确结果,再叠加摆动。</summary>
    private Quaternion ArmRestRotation(string upperArm, string lowerArm, Vector3 tunedAxis, float tunedAngle, float sway)
    {
        var assumedSign = upperArm.StartsWith("left", StringComparison.Ordinal) ? 1f : -1f;
        var sign = assumedSign;
        if (Model.HumanBones.TryGetValue(lowerArm, out var l))
        {
            var lx = Model.Nodes[l].LocalTranslation.X;
            if (Math.Abs(lx) > 1e-4f)
                sign = Math.Sign(lx);
        }
        if (sign == assumedSign)
            return QuaternionFromAxis(tunedAxis, tunedAngle + sway); // 手调朝向:原样(逐帧一致)

        // 朝向镜像:下垂 + 本侧外张,前后不偏(垂直落体侧,与躯干齐平)
        // ——偏前会悬在身前、偏后会垂到身后,都显假
        var assumedHang = QuaternionFromAxis(tunedAxis, tunedAngle) * (assumedSign * Vector3.UnitX);
        var target = Vector3.Normalize(new Vector3(sign * Math.Abs(assumedHang.X), assumedHang.Y, 0f));
        var start = new Vector3(sign, 0f, 0f);
        var rot = FromToRotation(start, target);
        if (Math.Abs(sway) > 1e-5f)
        {
            var axis = Vector3.Cross(start, target);
            if (axis.LengthSquared > 1e-10f)
                rot = QuaternionFromAxis(Vector3.Normalize(axis), sway) * rot;
        }
        return rot;
    }

    /// <summary>手臂局部朝向与手调(VRM1:左 +X / 右 -X)是否一致:一致返回 1,镜像(VRM0)返回 -1。
    /// 依据下臂相对上臂的局部平移 X 符号判定,供上臂/前臂等按朝向镜像对调使用。</summary>
    private int ArmMirrorFactor(string upperArm, string lowerArm)
    {
        var assumed = upperArm.StartsWith("left", StringComparison.Ordinal) ? 1 : -1;
        if (Model.HumanBones.TryGetValue(lowerArm, out var l))
        {
            var lx = Model.Nodes[l].LocalTranslation.X;
            if (Math.Abs(lx) > 1e-4f)
                return Math.Sign(lx) == assumed ? 1 : -1;
        }
        return 1;
    }

    // 表情权重超过此值视为"已激活":此时其 morph 已显著位移眼皮,内建眨眼叠加会穿模。
    private const float BlinkSuppressWeight = 0.5f;

    /// <summary>当前是否有"重塑眼部"的表情处于激活态(权重 ≥ 阈值),此时内建眨眼会与表情 morph 冲突而穿模。
    /// 按 VRM 约定 "neutral" 是中性静息脸(不重塑眼皮),予以排除;其余情绪/自定义表情(如笑眼、困倦)
    /// 激活即抑制眨眼,待其权重回落后再自然眨眼。</summary>
    private bool ExpressionSuppressesBlink()
    {
        foreach (var kv in _weights)
        {
            if (kv.Value < BlinkSuppressWeight)
                continue;
            if (!Model.Expressions.TryGetValue(kv.Key, out var expr))
                continue;
            if (expr.Category is not ("emotion" or "custom"))
                continue;
            if (kv.Key.Equals("neutral", StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }

    // 程序化随机 idle 各通道的角度范围(弧度):名称 → (min, max)。幅度刻意偏小,保持"自然微动"而非夸张动作。
    // 注意:髋部不设旋转通道——腿/脚必须稳稳站地,重心摆动全部由躯干(胸/脊柱)以上承担
    private static readonly (string name, float min, float max)[] IdleChannelRanges =
    {
        ("chestTwist",-0.080f, 0.080f),  // 躯干扭转(胸绕 Y,兼重心左右移)
        ("headRoll",  -0.070f, 0.070f),  // 头侧倾(头绕 Z)
    };

    // 手臂自然下垂的基础角度(弧度):把上臂从张臂的默认姿势旋到体侧,避免待机时僵硬的 T-pose 张臂。
    // 本模型文件为 T-pose(手臂水平)且肩点(±0.109)比髋部(±0.19)窄,手臂垂直会穿进躯干;
    // 轴/角按模型几何(含根节点 π 朝向修正+躯干宽度)求解:上臂外张 7.5°+前倾 27°,
    // 手肘前屈后手掌自然落在裙侧前方,前臂/手掌各采样点均不穿模
    private const float ArmRestAngle = 1.3837f;
    private static readonly Vector3 ArmRestAxisLeft = new(0f, 0.1f, -0.889f);
    private static readonly Vector3 ArmRestAxisRight = new(0f, -0.1f, 0.889f);
    // 手肘微屈(弧度):让手臂不笔直,前臂略向前收,呈现放松站姿。
    private const float ElbowBendAngle = 0.40f;
    // 手指自然微屈(弧度,向掌心卷):proximal/intermediate/distal 三节递减 + 拇指,构成松握拳。
    private const float FingerCurlProximal = 0.46f;
    private const float FingerCurlMid = 0.34f;
    private const float FingerCurlDistal = 0.20f;
    private const float ThumbCurl = 0.46f;
    // 拇指收拢幅度比例(相对 ThumbCurl,根部/近端关节各一):目标 = 拇指各段方向与「实际待机姿态
    // 下(四指已卷曲)的食指方向」平行贴拢。1.0 ≈ 夹角 8°(自然搭放);参考点必须用卷曲后的食指,
    // 用静止位置会因食指实际内卷而让拇指向外甩。超过 1.2 无进一步收益。
    private const float ThumbRootTuckRatio = 0.9f;
    private const float ThumbProxTuckRatio = 0.9f;

    /// <summary>更新随机 idle 姿态:每隔 2~5 秒随机挑新目标(每通道 75% 概率更新、其余保持,制造局部动/静的自然感),
    /// 并对当前值做指数缓动(约 1.5s 到位)。首次调用惰性初始化。</summary>
    private void UpdateIdlePose(float time, float dt)
    {
        if (_idlePose.Count == 0)
        {
            foreach (var (name, _, _) in IdleChannelRanges)
                _idlePose[name] = (0f, 0f);
            _microPhase = (float)(_random.NextDouble() * MathF.Tau);
            _microFreq = 0.5f + _random.NextSingle() * 0.7f;
            _armSwayPhase = (float)(_random.NextDouble() * MathF.Tau);
            _idleNextSwitch = 1f + _random.NextSingle() * 2f;
        }

        if (time >= _idleNextSwitch)
        {
            foreach (var (name, min, max) in IdleChannelRanges)
            {
                if (_random.NextDouble() < 0.75f)
                {
                    var (cur, _) = _idlePose[name];
                    _idlePose[name] = (cur, min + _random.NextSingle() * (max - min));
                }
            }
            _idleNextSwitch = time + 2f + _random.NextSingle() * 3f;
        }

        var k = 1f - MathF.Exp(-dt * 2.2f); // 指数缓动趋近目标
        foreach (var kv in _idlePose)
        {
            var (cur, tgt) = kv.Value;
            _idlePose[kv.Key] = (cur + (tgt - cur) * k, tgt);
        }
    }

    /// <summary>把 idle 姿态(呼吸、重心、环视、微动、手臂下垂、手肘微屈、手指微屈)按 humanoid 名写入 _idleRotations,
    /// 供 LocalRotationOf 应用。宿主对该人骨的覆盖仍优先(见 LocalRotationOf)。</summary>
    private void ComposeIdleRotations(float time)
    {
        void Get(string name, out float v)
        {
            _idlePose.TryGetValue(name, out var p);
            v = p.cur;
        }
        Get("chestTwist", out var chestTwist);
        Get("headRoll", out var headRoll);

        // 微动噪声:低频小幅,让身体从不完全静止
        var micro = MathF.Sin(time * _microFreq + _microPhase) * 0.008f;

        // 躯干(呼吸 + 扭转 + 重心):chest/upperChest/spine 都套用;
        // 髋部保持不动——腿部稳稳站地,摆动只发生在躯干以上
        if (Model.HumanBones.ContainsKey("chest") || Model.HumanBones.ContainsKey("upperChest") || Model.HumanBones.ContainsKey("spine"))
        {
            var breathe = MathF.Sin(time * 1.6f) * 0.009f; // 呼吸(躯干绕 X 微动)
            var chestRot = QuaternionFromAxis(Vector3.UnitY, chestTwist) * QuaternionFromAxis(Vector3.UnitX, breathe);
            foreach (var k in new[] { "chest", "upperChest", "spine" })
                if (Model.HumanBones.ContainsKey(k))
                    _idleRotations[k] = chestRot;
        }

        if (Model.HumanBones.ContainsKey("head"))
        {
            var g = _gazeCurrent;
            var gaze = QuaternionFromAxis(Vector3.UnitY, g.X) * QuaternionFromAxis(Vector3.UnitX, -g.Y);
            _idleRotations["head"] = gaze * QuaternionFromAxis(Vector3.UnitZ, headRoll + micro);
        }

        // 上臂:从 T-pose 水平旋到体侧下垂(绕肩骨求解出的轴,略带前倾),叠加轻微摆动
        var swayL = MathF.Sin(time * 0.55f + _armSwayPhase) * 0.035f;
        var swayR = MathF.Sin(time * 0.55f + _armSwayPhase + 1.6f) * 0.035f;
        if (Model.HumanBones.ContainsKey("leftUpperArm"))
            _idleRotations["leftUpperArm"] = ArmRestRotation("leftUpperArm", "leftLowerArm", ArmRestAxisLeft, ArmRestAngle, swayL);
        if (Model.HumanBones.ContainsKey("rightUpperArm"))
            _idleRotations["rightUpperArm"] = ArmRestRotation("rightUpperArm", "rightLowerArm", ArmRestAxisRight, ArmRestAngle, swayR);

        // 前臂:手肘微屈(左绕 Y 负、右正)
        if (Model.HumanBones.ContainsKey("leftLowerArm"))
            _idleRotations["leftLowerArm"] = QuaternionFromAxis(Vector3.UnitY, -ElbowBendAngle);
        if (Model.HumanBones.ContainsKey("rightLowerArm"))
            _idleRotations["rightLowerArm"] = QuaternionFromAxis(Vector3.UnitY, ElbowBendAngle);

        // 手指:向掌心自然微屈;各指(含拇指)随手臂朝向镜像对调
        CurlFingers("left", -1f, ArmMirrorFactor("leftUpperArm", "leftLowerArm"));
        CurlFingers("right", 1f, ArmMirrorFactor("rightUpperArm", "rightLowerArm"));
    }

    /// <summary>手指自然微屈:4 指各三节向掌心卷(绕手骨局部 Z,幅度递减)+ 拇指。
    /// 各指随手臂朝向镜像对调。</summary>
    private void CurlFingers(string side, float originalSign, int mirror)
    {
        var fingerSign = originalSign * mirror; // 镜像手臂卷曲方向对调
        foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
        {
            SetFinger(side + f + "Proximal", FingerCurlProximal * fingerSign);
            SetFinger(side + f + "Intermediate", FingerCurlMid * fingerSign);
            SetFinger(side + f + "Distal", FingerCurlDistal * fingerSign);
        }
        // 拇指:增量按几何预计算(见 ComputeThumbCurlDeltas)——拇指局部轴因模型而异,
        // 固定局部 Z 在部分模型(VRM0 等)上会把拇指扭向背离食指的方向而非收拢;
        // 根部(掌骨关节)按比例参与收拢,构成放松手型的整段自然搭放
        if (_thumbCurlDelta.TryGetValue(side, out var thumbDelta))
        {
            _idleRotations[side + "ThumbProximal"] = thumbDelta.Proximal;
            if (thumbDelta.Metacarpal != Quaternion.Identity && Model.HumanBones.ContainsKey(side + "ThumbMetacarpal"))
                _idleRotations[side + "ThumbMetacarpal"] = thumbDelta.Metacarpal;
        }
    }

    private void SetFinger(string humanoidKey, float angle)
    {
        if (angle == 0f || !Model.HumanBones.ContainsKey(humanoidKey))
            return;
        _idleRotations[humanoidKey] = QuaternionFromAxis(Vector3.UnitZ, angle);
    }

    private void UpdateExpressionWeights(float dt)
    {
        var k = Math.Clamp(dt * 8f, 0f, 1f); // 平滑趋近目标
        foreach (var key in Model.Expressions.Keys)
        {
            _weightTargets.TryGetValue(key, out var target);
            _weights.TryGetValue(key, out var current);
            current += (target - current) * k;
            if (MathF.Abs(current) < 0.001f)
                current = 0f;
            _weights[key] = current;
        }
    }

    /// <summary>节点局部旋转:宿主 humanoid 覆盖(优先)→ 内建随机 idle → 文件默认。</summary>
    private Quaternion LocalRotationOf(VrmNode node)
    {
        foreach (var kv in Model.HumanBones)
        {
            if (kv.Value != node.Index)
                continue;
            if (_poseOverrides.TryGetValue(kv.Key, out var q))
                return node.LocalRotation * q; // 宿主人骨覆盖优先
            if (_animationRotations.TryGetValue(kv.Key, out var anim))
            {
                if (_animWeight >= 1f)
                    return anim; // 动画满速:重定向后的局部旋转(替换静止姿势)
                // 淡入/淡出:在「无动画姿势」(idle 或静止 bind)与动画姿势之间平滑过渡
                var rest = _idleRotations.TryGetValue(kv.Key, out var idleRot) ? node.LocalRotation * idleRot : node.LocalRotation;
                return Quaternion.Slerp(rest, anim, Math.Clamp(_animWeight, 0f, 1f));
            }
            if (_idleRotations.TryGetValue(kv.Key, out var idle))
                return node.LocalRotation * idle; // 内建随机 idle(呼吸/重心/环视/微动/手臂/手指)
        }
        return node.LocalRotation;
    }

    /// <summary>节点局部平移:VRMA 动画(已按模型臀高缩放)命中则替换(随动画权重淡入淡出),否则文件默认。</summary>
    private Vector3 LocalTranslationOf(VrmNode node)
    {
        foreach (var kv in Model.HumanBones)
        {
            if (kv.Value != node.Index)
                continue;
            if (_animationTranslations.TryGetValue(kv.Key, out var t))
            {
                if (_animWeight >= 1f)
                    return t;
                return Vector3.Lerp(node.LocalTranslation, t, Math.Clamp(_animWeight, 0f, 1f));
            }
        }
        return node.LocalTranslation;
    }

    /// <summary>自动检测模型朝向:leftEye 相对 head 的 Z 分量 &gt;0 说明脸朝 +Z(相机在 -Z 侧,需转 π),
    /// &lt;0 说明脸朝 -Z(保持 0)。无眼骨或分量过小时按世代兜底。</summary>
    private float DetectYawOffset()
    {
        if (Model.HumanBones.TryGetValue("leftEye", out var eye)
            && Model.HumanBones.TryGetValue("head", out var head))
        {
            var d = ExtractTranslation(Model.Nodes[eye].World) - ExtractTranslation(Model.Nodes[head].World);
            if (Math.Abs(d.Z) > 1e-4f)
                return d.Z > 0f ? MathF.PI : 0f;
        }
        return Model.SpecVersion == 1 ? MathF.PI : 0f;
    }

    private void UpdateWorlds()
    {
        var yawFix = ModelYawOffset != 0f ? QuaternionFromAxis(Vector3.UnitY, ModelYawOffset) : Quaternion.Identity;
        // 根节点(无父)自底向上递归
        for (var i = 0; i < Model.Nodes.Count; i++)
        {
            var node = Model.Nodes[i];
            if (node.Parent >= 0)
                continue;
            var rootRot = node.LocalRotation * yawFix;
            node.World = Compose(node.LocalTranslation, rootRot, node.LocalScale);
            PropagateWorld(i, yawFix);
        }
    }

    private void PropagateWorld(int nodeIndex, Quaternion yawFix)
    {
        var node = Model.Nodes[nodeIndex];
        foreach (var c in node.Children)
        {
            var child = Model.Nodes[c];
            var rot = LocalRotationOf(child);
            child.World = MatMul(node.World, Compose(LocalTranslationOf(child), rot, child.LocalScale));
            PropagateWorld(c, yawFix);
        }
    }

    // ───────── 弹簧骨骼(verlet) ─────────

    private void UpdateSpringBones(float dt, bool initialize)
    {
        dt = Math.Min(dt, 1f / 30f);
        foreach (var group in Model.SpringGroups)
        {
            // 碰撞球(世界空间)
            var colliders = SpringCollidersEnabled
                ? group.Colliders
                    .Where(c => c.Node >= 0 && c.Node < Model.Nodes.Count)
                    .Select(c => (center: XformPoint(Model.Nodes[c.Node].World, c.Offset), radius: c.Radius))
                    .ToList()
                : new List<(Vector3 center, float radius)>();

            foreach (var joint in group.Joints)
            {
                var node = Model.Nodes[joint.Node];
                var nodeWorld = node.World; // joint 节点自身世界(父链已含前面 joint 的修正)

                // tail 方向与长度:取第一个子节点的初始局部位置;无子则按自身方向延伸
                Vector3 tailLocal;
                if (joint.TailNode >= 0)
                {
                    var tail = Model.Nodes[joint.TailNode];
                    tailLocal = tail.LocalTranslation;
                }
                else
                {
                    tailLocal = node.LocalTranslation * 0.5f; // 链尾:按父方向延伸一半
                }
                var tailLen = tailLocal.Length;
                if (tailLen < 1e-6f)
                    continue;
                var tailDirLocal = Vector3.Normalize(tailLocal);

                // 基准目标:joint 节点世界 × tail 局部 = tail 的默认世界位置
                var target = XformPoint(nodeWorld, tailLocal);

                if (initialize || !joint.Initialized)
                {
                    joint.Current = target;
                    joint.Previous = target;
                    joint.Initialized = true;
                    continue;
                }

                var stiffness = MathF.Max(0.01f, joint.Stiffness) * 6f;
                var drag = Math.Clamp(1f - joint.DragForce, 0f, 0.99f);
                var gravity = joint.GravityDir * joint.GravityPower;

                var velocity = (joint.Current - joint.Previous) * drag;
                if (velocity.LengthSquared > 0.09f) // 速度上限 0.3m/帧:防低帧率与异常放大
                    velocity = Vector3.Normalize(velocity) * 0.3f;
                var next = joint.Current + velocity + (target - joint.Current) * Math.Clamp(stiffness * dt, 0f, 0.9f) + gravity * dt * dt;

                // 长度约束:骨骼根 = joint 节点自身的世界位置(列向量约定取 M14/M24/M34)
                var rootPos = ExtractTranslation(nodeWorld);
                var dir = next - rootPos;
                if (dir.LengthSquared < 1e-10f)
                    dir = XformNormal(nodeWorld, tailDirLocal);
                next = rootPos + Vector3.Normalize(dir) * tailLen;

                // 碰撞球推出
                foreach (var c in colliders)
                {
                    var d = next - c.center;
                    var minDist = c.radius + joint.HitRadius;
                    if (d.LengthSquared < minDist * minDist && d.LengthSquared > 1e-10f)
                        next = c.center + Vector3.Normalize(d) * minDist;
                }

                // 碰撞后再次长度约束(碰撞可能破坏骨长)
                dir = next - rootPos;
                if (dir.LengthSquared > 1e-10f)
                    next = rootPos + Vector3.Normalize(dir) * tailLen;

                // 漂移上限:距基准目标超过 0.5m 拉回(防长时间累积漂移把头发拉到身前)
                var drift = next - target;
                if (drift.LengthSquared > 0.25f)
                    next = target + Vector3.Normalize(drift) * 0.5f;

                joint.Previous = joint.Current;
                joint.Current = next;

                // 修正节点世界旋转:让 tail 指向模拟位置(绕骨骼自身原点,平移不变)
                var worldDir = Vector3.Normalize(joint.Current - rootPos);
                var currentDir = XformNormal(nodeWorld, tailDirLocal);
                var fix = FromToRotation(currentDir, worldDir);
                node.World = Compose(ExtractTranslation(nodeWorld),
                    fix * ToQuaternion(nodeWorld), ExtractScale(nodeWorld));
            }
        }
    }

    // ───────── 表情 morph + CPU 蒙皮 ─────────

    private void UpdateMorphedPositions(VrmPrimitive p, Vector3[] positions, Vector3[] normals)
    {
        Array.Copy(p.BasePositions, positions, positions.Length);
        if (p.BaseNormals.Length == positions.Length)
            Array.Copy(p.BaseNormals, normals, normals.Length);
        else
            Array.Fill(normals, Vector3.UnitZ);

        // 激活表情的 morph 叠加(delta)
        var hasMorph = false;
        foreach (var expr in Model.Expressions.Values)
        {
            if (!_weights.TryGetValue(expr.Key, out var w) || w <= 0.001f)
                continue;
            foreach (var (pi, ti, bw) in expr.MorphBinds)
            {
                if (pi != Model.Primitives.IndexOf(p))
                    continue;
                w = Math.Clamp(bw * w, 0f, 1f);
                if (ti < p.TargetPositions.Length && p.TargetPositions[ti].Length == positions.Length)
                {
                    var dp = p.TargetPositions[ti];
                    for (var i = 0; i < positions.Length; i++)
                        positions[i] += dp[i] * w;
                    hasMorph = true;
                }
                if (ti < p.TargetNormals.Length && p.TargetNormals[ti].Length == normals.Length)
                {
                    var dn = p.TargetNormals[ti];
                    for (var i = 0; i < normals.Length; i++)
                        normals[i] += dn[i] * w;
                }
            }
        }
        if (hasMorph)
            for (var i = 0; i < normals.Length; i++)
                normals[i] = Vector3.Normalize(normals[i]);
    }

    /// <summary>全部 primitive:morph + 蒙皮 → 交错输出缓冲(pos3/normal3/uv2)。</summary>
    public void SkinAll()
    {
        var positions = Array.Empty<Vector3>();
        var normals = Array.Empty<Vector3>();
        var joints = Array.Empty<Matrix4>();
        foreach (var p in Model.Primitives)
        {
            var count = p.BasePositions.Length;
            if (positions.Length < count)
            {
                positions = new Vector3[count];
                normals = new Vector3[count];
            }
            UpdateMorphedPositions(p, positions, normals);

            // 关节矩阵 = 节点世界 × 逆绑定
            if (p.SkinJoints.Length > 0 && p.InverseBinds.Length == p.SkinJoints.Length)
            {
                if (joints.Length < p.SkinJoints.Length)
                    joints = new Matrix4[p.SkinJoints.Length];
                for (var i = 0; i < p.SkinJoints.Length; i++)
                    joints[i] = MatMul(Model.Nodes[p.SkinJoints[i]].World, p.InverseBinds[i]);
            }

            var outBuf = p.Skinned;
            for (var i = 0; i < count; i++)
            {
                Vector3 pos, nrm;
                if (p.SkinJoints.Length > 0 && i < p.Joints.Length)
                {
                    var j = p.Joints[i];
                    var w = p.Weights[i];
                    var m0 = joints[(int)j.X];
                    var m1 = joints[(int)j.Y];
                    var m2 = joints[(int)j.Z];
                    var m3 = joints[(int)j.W];
                    var mat = new Matrix4(
                        m0.Row0 * w.X + m1.Row0 * w.Y + m2.Row0 * w.Z + m3.Row0 * w.W,
                        m0.Row1 * w.X + m1.Row1 * w.Y + m2.Row1 * w.Z + m3.Row1 * w.W,
                        m0.Row2 * w.X + m1.Row2 * w.Y + m2.Row2 * w.Z + m3.Row2 * w.W,
                        m0.Row3 * w.X + m1.Row3 * w.Y + m2.Row3 * w.Z + m3.Row3 * w.W);
                    pos = XformPoint(mat, positions[i]);
                    nrm = XformNormal(mat, normals[i]);
                }
                else
                {
                    pos = positions[i];
                    nrm = normals[i];
                }
                var o = i * 8;
                outBuf[o] = pos.X; outBuf[o + 1] = pos.Y; outBuf[o + 2] = pos.Z;
                outBuf[o + 3] = nrm.X; outBuf[o + 4] = nrm.Y; outBuf[o + 5] = nrm.Z;
                outBuf[o + 6] = i < p.Uvs.Length ? p.Uvs[i].X : 0f;
                outBuf[o + 7] = i < p.Uvs.Length ? p.Uvs[i].Y : 0f;
            }
        }
    }

    /// <summary>情绪类表情 key(情绪映射与动作列表用;排除眨眼/口型/视线/系统系)。</summary>
    public string[] GetEmotionKeys() =>
        Model.Expressions.Values.Where(e => e.Category is "emotion" or "custom").Select(e => e.Key).OrderBy(k => k).ToArray();

    public string[] GetExpressionKeys() =>
        Model.Expressions.Values.Where(e => e.Category is not "look").Select(e => e.Key).OrderBy(k => k).ToArray();
}
