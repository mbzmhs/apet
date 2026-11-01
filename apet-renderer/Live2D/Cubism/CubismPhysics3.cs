using System.Text.Json;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>
/// .physics3.json 物理演算——官方 Framework 算法的忠实移植(参照 CubismWebFramework/src/physics):
/// 输入参数先按各自量纲归一化,再累加为"锚点平移"(X/Y)与"重力方向旋转角"(Angle);
/// 每个链节做"惯性 + 力"积分后投影回以父节点为心、Radius 为半径的球面(刚性约束),
/// 速度 = 上帧位移 × Mobility,延迟因子 delay = Delay·dt·30(官方 30fps 基准);
/// 静止时链条沿重力方向自然下垂、输出为零,因此头发/飘带摆动后会自动回位。
/// 输出 = 相邻链节间有符号夹角(弧度)或位移 × Scale,直接写回参数(权重 100% 为覆盖)。
/// 常量与官方一致:AirResistance=5.0、MaximumWeight=100.0、MovementThreshold=0.001。
/// </summary>
public class CubismPhysics3
{
    const float AirResistance = 5.0f;
    const float MaximumWeight = 100.0f;
    const float MovementThreshold = 0.001f;

    class Input
    {
        public string ParamId;
        public int Type;            // 0 = X, 1 = Y, 2 = Angle
        public float Weight;        // JSON 原值 0..100
        public bool Reflect;
        public CubismParameter Param;   // Bind 时解析
    }

    class Output
    {
        public string ParamId;
        public int Type;            // 0 = X, 1 = Y, 2 = Angle
        public int VertexIndex;     // 1 起始
        public float Scale;
        public float Weight;        // JSON 原值 0..100
        public bool Reflect;
        public CubismParameter Param;   // Bind 时解析
    }

    class Particle
    {
        public float Radius;        // 与父节点的链节长度
        public float Mobility;      // 0..1,越大越"轻"
        public float Delay;         // 响应滞后(官方不钳制,可为 0)
        public float Acceleration;  // 力的大小系数
        // 运行状态(位置为相对锚点的抽象空间坐标,+y 为链条静止方向)
        public float PosX, PosY;
        public float LastGX, LastGY;   // 上一帧的重力方向
        public float VelX, VelY;       // 速度(=上帧位移/Delay因子×Mobility)
    }

    class Setting
    {
        public Input[] Inputs = Array.Empty<Input>();
        public Output[] Outputs = Array.Empty<Output>();
        public Particle[] Particles = Array.Empty<Particle>();   // [0] = 锚点,不模拟
        // 归一化区间(JSON Normalization 节)
        public float PosNormMin, PosNormMax, PosNormDef;
        public float AngNormMin, AngNormMax, AngNormDef;
    }

    Setting[] _settings = Array.Empty<Setting>();
    float _gravityX = 0f, _gravityY = -1f;   // Options.Gravity(Meta EffectiveForces)
    float _windX = 0f, _windY = 0f;          // Options.Wind
    float _fps;                              // Meta Fps(缺省 0 = 按帧时间步进)

    public bool IsEmpty => _settings.Length == 0;
    public bool Enabled { get; set; } = true;

    public static CubismPhysics3 Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var p = new CubismPhysics3();

        if (root.TryGetProperty("Meta", out var meta))
        {
            if (meta.TryGetProperty("Fps", out var fps)) p._fps = fps.GetSingle();
            if (meta.TryGetProperty("EffectiveForces", out var ef))
            {
                if (ef.TryGetProperty("Gravity", out var g))
                {
                    p._gravityX = g.TryGetProperty("X", out var gx) ? gx.GetSingle() : 0f;
                    p._gravityY = g.TryGetProperty("Y", out var gy) ? gy.GetSingle() : -1f;
                }
                if (ef.TryGetProperty("Wind", out var w))
                {
                    p._windX = w.TryGetProperty("X", out var wx) ? wx.GetSingle() : 0f;
                    p._windY = w.TryGetProperty("Y", out var wy) ? wy.GetSingle() : 0f;
                }
            }
        }

        if (root.TryGetProperty("PhysicsSettings", out var settings) && settings.ValueKind == JsonValueKind.Array)
        {
            var list = new List<Setting>();
            foreach (var sj in settings.EnumerateArray())
            {
                var s = new Setting();

                // 归一化区间(缺省 ±10/0,与官方编辑器导出一致)
                if (sj.TryGetProperty("Normalization", out var nj))
                {
                    if (nj.TryGetProperty("Position", out var pn))
                    {
                        s.PosNormMin = pn.TryGetProperty("Minimum", out var a) ? a.GetSingle() : -10f;
                        s.PosNormMax = pn.TryGetProperty("Maximum", out var b) ? b.GetSingle() : 10f;
                        s.PosNormDef = pn.TryGetProperty("Default", out var c) ? c.GetSingle() : 0f;
                    }
                    else { s.PosNormMin = -10f; s.PosNormMax = 10f; }
                    if (nj.TryGetProperty("Angle", out var an))
                    {
                        s.AngNormMin = an.TryGetProperty("Minimum", out var a) ? a.GetSingle() : -10f;
                        s.AngNormMax = an.TryGetProperty("Maximum", out var b) ? b.GetSingle() : 10f;
                        s.AngNormDef = an.TryGetProperty("Default", out var c) ? c.GetSingle() : 0f;
                    }
                    else { s.AngNormMin = -10f; s.AngNormMax = 10f; }
                }
                else { s.PosNormMin = s.AngNormMin = -10f; s.PosNormMax = s.AngNormMax = 10f; }

                var inputs = new List<Input>();
                if (sj.TryGetProperty("Input", out var ij) && ij.ValueKind == JsonValueKind.Array)
                    foreach (var e in ij.EnumerateArray())
                        inputs.Add(new Input
                        {
                            ParamId = GetId(e, "Source"),
                            Type = GetType(e.TryGetProperty("Type", out var t) ? t.GetString() : "X"),
                            Weight = e.TryGetProperty("Weight", out var w) ? w.GetSingle() : 100f,
                            Reflect = e.TryGetProperty("Reflect", out var r) && r.GetBoolean(),
                        });
                s.Inputs = inputs.ToArray();

                var outputs = new List<Output>();
                if (sj.TryGetProperty("Output", out var oj) && oj.ValueKind == JsonValueKind.Array)
                    foreach (var e in oj.EnumerateArray())
                        outputs.Add(new Output
                        {
                            ParamId = GetId(e, "Destination"),
                            Type = GetType(e.TryGetProperty("Type", out var t) ? t.GetString() : "X"),
                            VertexIndex = e.TryGetProperty("VertexIndex", out var vi) ? vi.GetInt32() : 1,
                            Scale = e.TryGetProperty("Scale", out var sc) ? sc.GetSingle() : 1f,
                            Weight = e.TryGetProperty("Weight", out var w) ? w.GetSingle() : 100f,
                            Reflect = e.TryGetProperty("Reflect", out var r) && r.GetBoolean(),
                        });
                s.Outputs = outputs.ToArray();

                // Vertices[0] 本身就是锚点(官方原样使用,不额外插入);其 Radius 不参与动力学
                var particles = new List<Particle>();
                if (sj.TryGetProperty("Vertices", out var vj) && vj.ValueKind == JsonValueKind.Array)
                    foreach (var e in vj.EnumerateArray())
                        particles.Add(new Particle
                        {
                            Radius = e.TryGetProperty("Radius", out var r) ? r.GetSingle() : 1f,
                            Mobility = e.TryGetProperty("Mobility", out var m) ? m.GetSingle() : 1f,
                            Delay = e.TryGetProperty("Delay", out var d) ? d.GetSingle() : 1f,
                            Acceleration = e.TryGetProperty("Acceleration", out var a) ? a.GetSingle() : 1f,
                        });
                s.Particles = particles.ToArray();

                list.Add(s);
            }
            p._settings = list.ToArray();
        }
        return p;
    }

    static string GetId(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var s) && s.TryGetProperty("Id", out var id) ? id.GetString() : null;

    static int GetType(string type) => type switch
    {
        "Angle" => 2,
        "Y" => 1,
        _ => 0,
    };

    /// <summary>模型加载完成后调用:解析参数引用并初始化链条静止姿态(沿 +y 伸直)。</summary>
    public void Bind(CubismModel model)
    {
        foreach (var s in _settings)
        {
            foreach (var i in s.Inputs) i.Param = model.GetParameter(i.ParamId);
            foreach (var o in s.Outputs) o.Param = model.GetParameter(o.ParamId);

            float y = 0f;
            for (int i = 0; i < s.Particles.Length; i++)
            {
                var p = s.Particles[i];
                if (i > 0) y += p.Radius;
                p.PosX = 0f; p.PosY = y;          // 官方 initialize:沿 +y 累计 Radius
                p.LastGX = 0f; p.LastGY = 1f;     // 官方:(0,-1) 取反 → (0,1)
                p.VelX = 0f; p.VelY = 0f;
            }
        }
    }

    /// <summary>每帧求值。dt 单位秒;Meta.Fps&gt;0 时按固定步长子步进。</summary>
    public void Evaluate(CubismModel model, float deltaTime)
    {
        if (!Enabled || _settings.Length == 0) return;
        deltaTime = Math.Clamp(deltaTime, 0.0005f, 0.1f);

        if (_fps > 0f)
        {
            // 固定步长子步进(官方 FPS 路径的简化:不做输出双缓冲插值)
            float step = 1f / _fps;
            int n = Math.Clamp((int)(deltaTime / step), 1, 8);
            float sdt = deltaTime / n;
            for (int i = 0; i < n; i++) Step(sdt);
        }
        else
        {
            Step(deltaTime);
        }
    }

    void Step(float dt)
    {
        foreach (var s in _settings)
        {
            // ---- 输入:归一化 → 锚点平移(X/Y)与重力旋转角(Angle,度) ----
            float totalAngle = 0f, tx = 0f, ty = 0f;
            foreach (var i in s.Inputs)
            {
                if (i.Param == null) continue;
                float norm = NormalizeInput(i.Param.Value,
                    i.Param.Minimum, i.Param.Maximum,
                    i.Type == 2 ? s.AngNormMin : s.PosNormMin,
                    i.Type == 2 ? s.AngNormMax : s.PosNormMax,
                    i.Type == 2 ? s.AngNormDef : s.PosNormDef,
                    i.Reflect);
                float w = i.Weight / MaximumWeight;
                switch (i.Type)
                {
                    case 0: tx += norm * w; break;
                    case 1: ty += norm * w; break;
                    default: totalAngle += norm * w; break;
                }
            }

            // 锚点平移绕 -totalAngle 旋转(官方 updateParticles 前的同款变换)
            float radA = -MathF.PI * totalAngle / 180f;
            float cosA = MathF.Cos(radA), sinA = MathF.Sin(radA);
            float ax = tx * cosA - ty * sinA;
            float ay = tx * sinA + ty * cosA;

            // 当前重力方向 = radianToDirection(deg2rad(totalAngle)) = (sin, cos)
            float gRad = MathF.PI * totalAngle / 180f;
            float cgx = MathF.Sin(gRad), cgy = MathF.Cos(gRad);

            var parts = s.Particles;
            parts[0].PosX = ax; parts[0].PosY = ay;   // 锚点直接跟随平移输入(无动力学)

            float thr = MovementThreshold * s.PosNormMax;
            for (int i = 1; i < parts.Length; i++)
            {
                var p = parts[i];
                float fx = cgx * p.Acceleration + _windX;
                float fy = cgy * p.Acceleration + _windY;
                float lpx = p.PosX, lpy = p.PosY;
                float delayF = p.Delay * dt * 30f;    // 官方:delay = Delay·dt·30(30fps 基准)

                // 当前链节方向(父 → 本),按重力变化角/AirResistance 旋转(链条"感受"重力转向)
                float dx = p.PosX - parts[i - 1].PosX;
                float dy = p.PosY - parts[i - 1].PosY;
                float radian = DirectionToRadian(p.LastGX, p.LastGY, cgx, cgy) / AirResistance;
                if (radian != 0f)
                {
                    float cr = MathF.Cos(radian), sr = MathF.Sin(radian);
                    float nx = dx * cr - dy * sr;
                    dy = dx * sr + dy * cr;
                    dx = nx;
                }
                p.PosX = parts[i - 1].PosX + dx;
                p.PosY = parts[i - 1].PosY + dy;

                // 惯性 + 力(delay² 缩放,官方原式)
                p.VelX *= delayF; p.VelY *= delayF;
                p.PosX += p.VelX + fx * delayF * delayF;
                p.PosY += p.VelY + fy * delayF * delayF;

                // 刚性约束:投影回以父节点为心、Radius 为半径的球面
                float ndx = p.PosX - parts[i - 1].PosX;
                float ndy = p.PosY - parts[i - 1].PosY;
                float len = MathF.Sqrt(ndx * ndx + ndy * ndy);
                if (len < 1e-6f) { ndx = 0f; ndy = 1f; len = 1f; }   // 退化:指向上方(初始方向)
                p.PosX = parts[i - 1].PosX + ndx / len * p.Radius;
                p.PosY = parts[i - 1].PosY + ndy / len * p.Radius;

                if (MathF.Abs(p.PosX) < thr) p.PosX = 0f;            // 官方:微小位移清零防抖

                if (delayF != 0f)
                {
                    p.VelX = (p.PosX - lpx) / delayF * p.Mobility;   // 速度 = 实际位移/delay×Mobility
                    p.VelY = (p.PosY - lpy) / delayF * p.Mobility;
                }
                p.LastGX = cgx; p.LastGY = cgy;
            }

            // ---- 输出:相邻链节夹角(弧度)/位移 × Scale,直接写回参数 ----
            foreach (var o in s.Outputs)
            {
                var param = o.Param;
                if (param == null) continue;
                int pi = o.VertexIndex;
                if (pi < 1 || pi >= parts.Length) continue;

                float sx = parts[pi].PosX - parts[pi - 1].PosX;
                float sy = parts[pi].PosY - parts[pi - 1].PosY;
                float v;
                switch (o.Type)
                {
                    case 2:   // Angle:本链节相对上一链节的有符号夹角(弧度);首节参考为 -重力方向
                    {
                        float rx, ry;
                        if (pi >= 2) { rx = parts[pi - 1].PosX - parts[pi - 2].PosX; ry = parts[pi - 1].PosY - parts[pi - 2].PosY; }
                        else { rx = -_gravityX; ry = -_gravityY; }
                        v = DirectionToRadian(rx, ry, sx, sy);
                        break;
                    }
                    case 0: v = sx; break;
                    default: v = sy; break;
                }
                if (o.Reflect) v = -v;
                v *= o.Scale;
                if (v < param.Minimum) v = param.Minimum;
                else if (v > param.Maximum) v = param.Maximum;

                float w = o.Weight / MaximumWeight;
                param.Value = w >= 1f ? v : param.Value * (1f - w) + v * w;
            }
        }
    }

    /// <summary>官方 normalizeParameterValue:按参数量程中点线性映射到归一化区间,默认取反。</summary>
    static float NormalizeInput(float value, float pMin, float pMax,
        float nMin, float nMax, float nDef, bool reflect)
    {
        float maxV = MathF.Max(pMax, pMin);
        if (value > maxV) value = maxV;
        float minV = MathF.Min(pMax, pMin);
        if (value < minV) value = minV;

        float maxN = MathF.Max(nMax, nMin), minN = MathF.Min(nMax, nMin);
        float midV = (minV + maxV) / 2f;
        float pv = value - midV;
        float result;
        if (pv > 0f)
        {
            float pLen = maxV - midV;
            result = pLen != 0f ? pv * ((maxN - nDef) / pLen) + nDef : 0f;
        }
        else if (pv < 0f)
        {
            float pLen = minV - midV;
            result = pLen != 0f ? pv * ((minN - nDef) / pLen) + nDef : 0f;
        }
        else result = nDef;

        return reflect ? result : -result;   // 官方:非 Reflect 一律取反
    }

    /// <summary>官方 directionToRadian:from→to 的有符号夹角,归一化到 [-π, π]。</summary>
    static float DirectionToRadian(float fx, float fy, float tx, float ty)
    {
        float ret = MathF.Atan2(ty, tx) - MathF.Atan2(fy, fx);
        while (ret < -MathF.PI) ret += 2f * MathF.PI;
        while (ret > MathF.PI) ret -= 2f * MathF.PI;
        return ret;
    }
}
