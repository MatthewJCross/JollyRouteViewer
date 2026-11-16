using System.IO;
using System.Text.Json;

namespace JollyRouteViewer.Services;

public sealed class MapSettings
{
    public string Provider { get; set; } = "cartoLight";
    public string MapTilerKey { get; set; } = "";
}

public sealed class MapSettingsService
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JollyRouteViewer", "map-settings.json");

    public MapSettings Settings { get; }

    public MapSettingsService()
    {
        try
        {
            Settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<MapSettings>(File.ReadAllText(FilePath)) ?? new MapSettings() : new MapSettings();
        }
        catch (Exception) { Settings = new MapSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
