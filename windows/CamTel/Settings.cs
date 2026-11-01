using System.Text.Json;

namespace CamTel;

public sealed class Settings
{
    public string LastHost { get; set; } = "";
    public bool LastUsb { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int OutputWidth { get; set; } = 1280;
    public int OutputHeight { get; set; } = 720;
    public int Rotation { get; set; }
    public bool Mirror { get; set; }
    public bool Fill { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamTel", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
