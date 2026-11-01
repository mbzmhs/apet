using System.Text.Json;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>
/// .pose3.json 部件姿态:同一组内的部件互斥显示(如"左手 A / 左手 B"),
/// 切换时按淡入淡出过渡,Link 部件跟随主体部件的不透明度。
/// </summary>
public class CubismPose3
{
    class PartDef
    {
        public string Id;
        public string[] Links = Array.Empty<string>();
        public float Current;      // 0..1 当前不透明度
    }

    class Group
    {
        public PartDef[] Parts;    // 组内互斥
    }

    Group[] _groups = Array.Empty<Group>();
    readonly List<(CubismPart part, PartDef def)> _bindings = new();

    public float FadeInTime { get; private set; } = 0.3f;
    public float FadeOutTime { get; private set; } = 0.3f;
    public bool IsEmpty => _groups.Length == 0;
    public bool Enabled { get; set; } = true;

    public static CubismPose3 Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var p = new CubismPose3();
        if (root.TryGetProperty("FadeInTime", out var fi)) p.FadeInTime = Math.Max(fi.GetSingle(), 0.01f);
        if (root.TryGetProperty("FadeOutTime", out var fo)) p.FadeOutTime = Math.Max(fo.GetSingle(), 0.01f);

        if (root.TryGetProperty("Parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            var groups = new List<Group>();
            foreach (var gj in parts.EnumerateArray())
            {
                if (gj.ValueKind != JsonValueKind.Array) continue;
                var defs = new List<PartDef>();
                foreach (var pj in gj.EnumerateArray())
                {
                    var def = new PartDef { Id = pj.TryGetProperty("Id", out var id) ? id.GetString() : null };
                    if (pj.TryGetProperty("Link", out var links) && links.ValueKind == JsonValueKind.Array)
                        def.Links = links.EnumerateArray().Select(l => l.GetString()).ToArray();
                    defs.Add(def);
                }
                if (defs.Count > 0) groups.Add(new Group { Parts = defs.ToArray() });
            }
            p._groups = groups.ToArray();
        }
        return p;
    }

    /// <summary>加载模型后调用:把部件 id 绑定到模型,初始不透明度从模型读取。</summary>
    public void Bind(CubismModel model)
    {
        _bindings.Clear();
        foreach (var g in _groups)
            foreach (var d in g.Parts)
            {
                var part = model.GetPart(d.Id);
                if (part == null) continue;
                d.Current = part.Opacity;
                _bindings.Add((part, d));
            }
    }

    /// <summary>
    /// 外部直接写部件不透明度时同步姿态内部镜像:Evaluate 以镜像值(d.Current)选组内胜者,
    /// 不同步的话外部写入(如宿主的部件透明度基线)会在下一帧被姿态目标覆盖回退。
    /// </summary>
    public void NotifyPartOpacity(string partId, float opacity)
    {
        foreach (var (part, def) in _bindings)
        {
            if (def.Id != partId) continue;
            def.Current = opacity;
            part.Opacity = opacity;
        }
    }

    /// <summary>每帧求值:互斥组内透明度最高的部件保持/淡入,其余淡出。</summary>
    public void Evaluate(CubismModel model, float deltaTime)
    {
        if (!Enabled || _groups.Length == 0) return;
        float dt = Math.Clamp(deltaTime, 0f, 0.1f);

        foreach (var g in _groups)
        {
            // 找组内当前不透明度最高者为目标
            PartDef target = null;
            float best = -1f;
            foreach (var d in g.Parts)
            {
                if (d.Current > best) { best = d.Current; target = d; }
            }
            if (target == null) continue;

            foreach (var d in g.Parts)
            {
                float goal = d == target ? 1f : 0f;
                float fade = goal > d.Current ? dt / FadeInTime : dt / FadeOutTime;
                d.Current = Math.Clamp(d.Current + MathF.Sign(goal - d.Current) * Math.Min(Math.Abs(goal - d.Current), fade), 0f, 1f);

                var part = model.GetPart(d.Id);
                if (part != null) part.Opacity = d.Current;

                // Link 部件跟随主体
                foreach (var linkId in d.Links)
                {
                    var link = model.GetPart(linkId);
                    if (link != null) link.Opacity = d.Current;
                }
            }
        }
    }
}
