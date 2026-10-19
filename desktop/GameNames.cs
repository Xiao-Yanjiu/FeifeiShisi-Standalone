using System.Text.Json;
namespace Trial;
internal static class GameNames
{
    static readonly Dictionary<string,string> Worlds = ReadWorlds();
    static Dictionary<string,string> ReadWorlds()
    {
        var a = typeof(GameNames).Assembly; var name = a.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("worlds.json"));
        if (name == null) return new(); using var stream = a.GetManifestResourceStream(name)!; return JsonSerializer.Deserialize<Dictionary<string,string>>(stream) ?? new();
    }
    public static string World(string id) => Worlds.TryGetValue(id, out var name) && name.Length > 0 ? name : id;
}
