using System.Text.Json;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>model3.json 中的一条动作定义。</summary>
public class CubismMotionDef
{
    public string File { get; set; }
    public string Name { get; set; }
    public float? FadeInTime { get; set; }
    public float? FadeOutTime { get; set; }
    public string Sound { get; set; }
    public string Text { get; set; }
}

/// <summary>model3.json 中的一条表情定义。</summary>
public class CubismExpressionDef
{
    public string Name { get; set; }
    public string File { get; set; }
}

/// <summary>命中区域(HitArea):名字 → Drawable Id。</summary>
public class CubismHitAreaDef
{
    public string Id { get; set; }
    public string Name { get; set; }
}

/// <summary>参数组(EyeBlink / LipSync 等自动控制分组)。</summary>
public class CubismGroupDef
{
    public string Target { get; set; }
    public string Name { get; set; }
    public string[] Ids { get; set; }
}

/// <summary>
/// .model3.json 的解析结果:文件引用相对 model3.json 所在目录。
/// </summary>
public class CubismModel3Json
{
    public string SourcePath { get; private set; }
    public string BaseDirectory { get; private set; }

    public string MocPath { get; private set; }
    public string[] TexturePaths { get; private set; } = Array.Empty<string>();
    public string PhysicsPath { get; private set; }
    public string PosePath { get; private set; }
    public string DisplayInfoPath { get; private set; }

    /// <summary>动作分组:组名 → 该组的动作列表(顺序与 json 中一致)。</summary>
    public Dictionary<string, List<CubismMotionDef>> Motions { get; private set; } = new();
    public List<CubismExpressionDef> Expressions { get; private set; } = new();
    public List<CubismHitAreaDef> HitAreas { get; private set; } = new();
    public List<CubismGroupDef> Groups { get; private set; } = new();

    public static CubismModel3Json Load(string path)
    {
        var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        var m = new CubismModel3Json
        {
            SourcePath = path,
            BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)),
        };

        if (root.TryGetProperty("FileReferences", out var files))
        {
            if (files.TryGetProperty("Moc", out var moc))
                m.MocPath = m.Resolve(moc.GetString());
            if (files.TryGetProperty("Textures", out var textures) && textures.ValueKind == JsonValueKind.Array)
                m.TexturePaths = textures.EnumerateArray().Select(t => m.Resolve(t.GetString())).ToArray();
            if (files.TryGetProperty("Physics", out var physics))
                m.PhysicsPath = m.Resolve(physics.GetString());
            if (files.TryGetProperty("Pose", out var pose))
                m.PosePath = m.Resolve(pose.GetString());
            if (files.TryGetProperty("DisplayInfo", out var display))
                m.DisplayInfoPath = m.Resolve(display.GetString());

            if (files.TryGetProperty("Motions", out var motions) && motions.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in motions.EnumerateObject())
                {
                    var list = new List<CubismMotionDef>();
                    if (group.Value.ValueKind == JsonValueKind.Array)
                        foreach (var def in group.Value.EnumerateArray())
                            list.Add(new CubismMotionDef
                            {
                                File = m.Resolve(def.TryGetProperty("File", out var f) ? f.GetString() : null),
                                Name = def.TryGetProperty("Name", out var n) ? n.GetString() : null,
                                FadeInTime = def.TryGetProperty("FadeInTime", out var fi) ? fi.GetSingle() : null,
                                FadeOutTime = def.TryGetProperty("FadeOutTime", out var fo) ? fo.GetSingle() : null,
                                Sound = def.TryGetProperty("Sound", out var s) ? s.GetString() : null,
                                Text = def.TryGetProperty("Text", out var t) ? t.GetString() : null,
                            });
                    m.Motions[group.Name] = list;
                }
            }

            if (files.TryGetProperty("Expressions", out var exps) && exps.ValueKind == JsonValueKind.Array)
                foreach (var def in exps.EnumerateArray())
                    m.Expressions.Add(new CubismExpressionDef
                    {
                        Name = def.TryGetProperty("Name", out var n) ? n.GetString() : null,
                        File = m.Resolve(def.TryGetProperty("File", out var f) ? f.GetString() : null),
                    });
        }

        if (root.TryGetProperty("HitAreas", out var hitAreas) && hitAreas.ValueKind == JsonValueKind.Array)
            foreach (var h in hitAreas.EnumerateArray())
                m.HitAreas.Add(new CubismHitAreaDef
                {
                    Id = h.TryGetProperty("Id", out var id) ? id.GetString() : null,
                    Name = h.TryGetProperty("Name", out var name) ? name.GetString() : null,
                });

        if (root.TryGetProperty("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
                m.Groups.Add(new CubismGroupDef
                {
                    Target = g.TryGetProperty("Target", out var t) ? t.GetString() : null,
                    Name = g.TryGetProperty("Name", out var n) ? n.GetString() : null,
                    Ids = g.TryGetProperty("Ids", out var ids) && ids.ValueKind == JsonValueKind.Array
                        ? ids.EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>(),
                });

        return m;
    }

    string Resolve(string relative)
        => string.IsNullOrEmpty(relative) ? null : Path.GetFullPath(Path.Combine(BaseDirectory, relative));
}
