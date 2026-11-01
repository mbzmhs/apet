using System.Text.Json;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>表情中一条参数覆盖定义。Blend:Add(加)/ Multiply(乘)/ Overwrite(覆盖)。</summary>
public class CubismExpressionParameter
{
    public string Id { get; set; }
    public float Value { get; set; }
    public string Blend { get; set; } = "Add";
}

/// <summary>
/// .exp3.json 表情:一组参数覆盖 + 淡入淡出。
/// </summary>
public class CubismExpression
{
    public string Name { get; set; }
    public string SourcePath { get; private set; }
    public float FadeInTime { get; set; } = 0.15f;
    public float FadeOutTime { get; set; } = 0.5f;
    public CubismExpressionParameter[] Parameters { get; private set; } = Array.Empty<CubismExpressionParameter>();

    public static CubismExpression Load(string path, string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        var e = new CubismExpression { SourcePath = path, Name = name };
        if (root.TryGetProperty("FadeInTime", out var fi)) e.FadeInTime = fi.GetSingle();
        if (root.TryGetProperty("FadeOutTime", out var fo)) e.FadeOutTime = fo.GetSingle();
        if (root.TryGetProperty("Parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            var list = new List<CubismExpressionParameter>();
            foreach (var p in ps.EnumerateArray())
                list.Add(new CubismExpressionParameter
                {
                    Id = p.TryGetProperty("Id", out var id) ? id.GetString() : null,
                    Value = p.TryGetProperty("Value", out var v) ? v.GetSingle() : 0f,
                    Blend = p.TryGetProperty("Blend", out var b) ? b.GetString() : "Add",
                });
            e.Parameters = list.ToArray();
        }
        return e;
    }

    /// <summary>按官方语义把表情应用到参数上(weight 为淡入淡出权重 0..1)。</summary>
    public void Apply(CubismModel model, float weight)
    {
        if (weight <= 0) return;
        foreach (var p in Parameters)
        {
            var param = model.GetParameter(p.Id);
            if (param == null) continue;
            switch (p.Blend)
            {
                case "Multiply":
                    param.Value = param.Value * (1f + (p.Value - 1f) * weight);
                    break;
                case "Overwrite":
                    param.Value += (p.Value - param.Value) * weight;
                    break;
                default: // Add
                    param.Value = param.Value + p.Value * weight;
                    break;
            }
        }
    }
}
