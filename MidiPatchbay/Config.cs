using System.Text.Json;

namespace MidiPatchbay;

public sealed class PortConfig
{
    public string Id { get; set; }
    public string Name { get; set; }
}

public sealed class RouteConfig
{
    public string From { get; set; }
    public string To { get; set; }
}

public sealed class AppConfig
{
    public List<PortConfig> Ports { get; set; } = new();
    public List<RouteConfig> Routes { get; set; } = new();
    /// <summary>Last seen display name for device endpoints, so offline devices still show in the matrix.</summary>
    public Dictionary<string, string> KnownNames { get; set; } = new();
    public bool HideClockInMonitor { get; set; } = true;
    /// <summary>System, Light or Dark.</summary>
    public string Theme { get; set; } = "System";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MidiPatchbay", "config.json");

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch { /* corrupt file: start fresh */ }
        return new AppConfig();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, true);
        }
        catch { /* best effort */ }
    }
}
