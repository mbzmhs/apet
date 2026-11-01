using System.Text.Json;
using System.IO;

namespace APet.Renderer.Live2D.Cubism;

/// <summary>cdi3.json 中参数/部件的显示名与分组(供宿主 UI 展示)。</summary>
public class CubismCdiInfo
{
    public class Item
    {
        public string Id;
        public string Name;
        public string GroupId;
    }

    public List<Item> Parameters { get; } = new();
    public List<Item> ParameterGroups { get; } = new();
    public List<Item> Parts { get; } = new();

    public static CubismCdiInfo Load(string path)
    {
        var info = new CubismCdiInfo();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;

        void ReadList(string prop, List<Item> target)
        {
            if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var e in arr.EnumerateArray())
                target.Add(new Item
                {
                    Id = e.TryGetProperty("Id", out var id) ? id.GetString() : null,
                    Name = e.TryGetProperty("Name", out var name) ? name.GetString() : null,
                    GroupId = e.TryGetProperty("GroupId", out var g) ? g.GetString() : null,
                });
        }

        ReadList("Parameters", info.Parameters);
        ReadList("ParameterGroups", info.ParameterGroups);
        ReadList("Parts", info.Parts);
        return info;
    }

    public string GetParameterName(string id) => Parameters.FirstOrDefault(p => p.Id == id)?.Name;
    public string GetGroupName(string groupId) => ParameterGroups.FirstOrDefault(p => p.Id == groupId)?.Name;
}
