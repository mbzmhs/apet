using System.Text.Json;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>
/// .motion3.json 动作:参数/部件曲线求值 + 淡入淡出权重。
/// 曲线段类型与官方格式一致(线性 / 贝塞尔 / 阶跃 / 反阶跃)。
/// </summary>
public class CubismMotion
{
    const int SegmentLinear = 0, SegmentBezier = 1, SegmentStepped = 2, SegmentInverseStepped = 3;

    public string Name { get; set; }
    public float Duration { get; private set; }
    public bool Loop { get; private set; }
    public float Fps { get; private set; } = 30f;
    public float FadeInTime { get; set; } = 1f;
    public float FadeOutTime { get; set; } = 1f;
    public string SourcePath { get; private set; }

    /// <summary>官方 V2 单次循环长度[s]:Duration + 1/Fps(尾部修正窗口)。</summary>
    public float LoopDuration => Duration + 1f / Fps;

    /// <summary>曲线目标类型:Parameter / Part / Model。</summary>
    class Curve
    {
        public string Target;
        public string Id;
        // 分段:每段为 (type, 起始t, 起始v, 段参数float[])
        public (int type, float t0, float v0, float[] p)[] Segments;
        public float StartValue;
    }

    Curve[] _curves = Array.Empty<Curve>();
    readonly Dictionary<string, Curve> _paramCurves = new(StringComparer.Ordinal);
    readonly Dictionary<string, Curve> _partCurves = new(StringComparer.Ordinal);

    public bool IsEmpty => _curves.Length == 0;

    public static CubismMotion Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var m = new CubismMotion { SourcePath = path };

        float duration = 0;
        if (root.TryGetProperty("Meta", out var meta))
        {
            if (meta.TryGetProperty("Duration", out var d)) duration = d.GetSingle();
            if (meta.TryGetProperty("Loop", out var l)) m.Loop = l.GetBoolean();
            if (meta.TryGetProperty("Fps", out var f)) m.Fps = Math.Max(f.GetSingle(), 1f);
        }
        m.Duration = Math.Max(duration, 0.01f);

        if (root.TryGetProperty("Curves", out var curves) && curves.ValueKind == JsonValueKind.Array)
        {
            var list = new List<Curve>();
            foreach (var cj in curves.EnumerateArray())
            {
                var c = new Curve
                {
                    Target = cj.TryGetProperty("Target", out var t) ? t.GetString() : "Parameter",
                    Id = cj.TryGetProperty("Id", out var id) ? id.GetString() : null,
                };
                if (c.Id == null || cj.TryGetProperty("Segments", out var segs) == false
                    || segs.ValueKind != JsonValueKind.Array) continue;

                var raw = segs.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (raw.Length < 2) continue;
                c.StartValue = raw[1];

                var segments = new List<(int, float, float, float[])>();
                int i = 2;
                float lastT = raw[0], lastV = raw[1];
                while (i < raw.Length)
                {
                    int type = (int)raw[i++];
                    switch (type)
                    {
                        case SegmentLinear: // t,v
                            segments.Add((type, lastT, lastV, new[] { raw[i], raw[i + 1] }));
                            lastT = raw[i]; lastV = raw[i + 1]; i += 2;
                            break;
                        case SegmentBezier: // c1t,c1v,c2t,c2v,t,v
                            segments.Add((type, lastT, lastV, new[] { raw[i], raw[i + 1], raw[i + 2], raw[i + 3], raw[i + 4], raw[i + 5] }));
                            lastT = raw[i + 4]; lastV = raw[i + 5]; i += 6;
                            break;
                        case SegmentStepped: // t,v
                            segments.Add((type, lastT, lastV, new[] { raw[i], raw[i + 1] }));
                            lastT = raw[i]; lastV = raw[i + 1]; i += 2;
                            break;
                        case SegmentInverseStepped: // t,v:先保持到 t 再跳变
                            segments.Add((type, lastT, lastV, new[] { raw[i], raw[i + 1] }));
                            lastT = raw[i]; lastV = raw[i + 1]; i += 2;
                            break;
                        default:
                            i = raw.Length; // 未知类型,放弃剩余段
                            break;
                    }
                }
                c.Segments = segments.ToArray();
                list.Add(c);
                var dict = c.Target == "Part" ? m._partCurves : m._paramCurves;
                dict[c.Id] = c; // 同 id 后者覆盖
            }
            m._curves = list.ToArray();
        }
        return m;
    }

    /// <summary>
    /// 求值某条曲线在 time 处的值。time 超出时长时取末值;
    /// loopEndTime&gt;0 且 time 落在 (末关键帧, loopEndTime) 内时,应用官方 V2 尾部修正(correctEndPoint):
    /// 按末段类型把值从末值拉回初值(线性/贝塞尔为线性混合,阶跃保持到换圈点,反阶跃立即跳回初值)。
    /// </summary>
    static float EvaluateCurve(Curve c, float time, float loopEndTime = 0f, float crossToStart = 0f)
    {
        float v = (c.Segments.Length == 0 || time <= c.Segments[0].t0)
            ? c.StartValue
            : EvaluateCurveRaw(c, time, loopEndTime);
        // 循环交叉淡化:把本圈值向初值混合(无缝数据上该混合不可见,非无缝数据变成平滑回位)
        return crossToStart > 0f ? Lerp(v, c.StartValue, crossToStart) : v;
    }

    static float EvaluateCurveRaw(Curve c, float time, float loopEndTime)
    {
        foreach (var (type, t0, v0, p) in c.Segments)
        {
            switch (type)
            {
                case SegmentLinear:
                    if (time <= p[0]) return Lerp(v0, p[1], InverseLerp(t0, p[0], time));
                    break;
                case SegmentBezier:
                    if (time <= p[4])
                    {
                        float u = InverseLerp(t0, p[4], time);
                        return CubicBezier(v0, p[1], p[3], p[5], u);
                    }
                    break;
                case SegmentStepped:
                    if (time <= p[0]) return v0;          // 保持到该点后跳到新值
                    break;
                case SegmentInverseStepped:
                    if (time <= p[0]) return p[1];        // 先跳到新值,到点前保持
                    break;
            }
        }

        // time 越过末关键帧:官方一律取末值(各段类型在段结束后都保持末值)
        var last = c.Segments[^1];
        float endValue = last.p[^1];
        if (loopEndTime > 0f && time < loopEndTime)
        {
            switch (last.type)
            {
                case SegmentStepped: return endValue;              // 保持旧值到换圈点
                case SegmentInverseStepped: return c.StartValue;   // 立即跳回初值
                default:                                              // 线性/贝塞尔:末值 → 初值线性混合
                    float lastKeyT = last.type == SegmentBezier ? last.p[4] : last.p[0];
                    float u = (time - lastKeyT) / Math.Max(loopEndTime - lastKeyT, 1e-6f);
                    return Lerp(endValue, c.StartValue, Math.Clamp(u, 0f, 1f));
            }
        }
        return endValue;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static float InverseLerp(float a, float b, float v)
        => Math.Abs(b - a) < 1e-8f ? 0f : Math.Clamp((v - a) / (b - a), 0f, 1f);

    static float CubicBezier(float v0, float v1, float v2, float v3, float u)
    {
        float o = 1 - u;
        return o * o * o * v0 + 3 * o * o * u * v1 + 3 * o * u * u * v2 + u * u * u * v3;
    }

    /// <summary>参数曲线求值(不存在该曲线返回 false)。loopEndTime 用于循环播放的尾部修正,crossToStart 为循环交叉淡化权重。</summary>
    public bool EvaluateParameter(string paramId, float time, out float value, float loopEndTime = 0f, float crossToStart = 0f)
    {
        if (_paramCurves.TryGetValue(paramId, out var c))
        {
            value = EvaluateCurve(c, time, loopEndTime, crossToStart);
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>部件不透明度曲线求值(不存在该曲线返回 false)。loopEndTime 用于循环播放的尾部修正,crossToStart 为循环交叉淡化权重。</summary>
    public bool EvaluatePart(string partId, float time, out float value, float loopEndTime = 0f, float crossToStart = 0f)
    {
        if (_partCurves.TryGetValue(partId, out var c))
        {
            value = EvaluateCurve(c, time, loopEndTime, crossToStart);
            return true;
        }
        value = 0;
        return false;
    }
}
