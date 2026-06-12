using System.Text.Json;

namespace EarShare.Settings;

public sealed class SavedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Volume { get; set; } = 100;
    public int DelayMs { get; set; } = 0;
}

/// <summary>Persisted device list + volumes, so the family setup survives restarts.</summary>
public sealed class AppSettings
{
    public List<SavedDevice> Devices { get; set; } = new();

    /// <summary>Capture source endpoint ID; null = follow the Windows default output.</summary>
    public string? CaptureDeviceId { get; set; }

    /// <summary>Per-device buffer target in ms — latency vs. stutter robustness.</summary>
    public int BufferMs { get; set; } = 40;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EarShare", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // corrupt/unreadable settings -> start fresh
        }
        return new AppSettings();
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
            // settings persistence is best-effort
        }
    }
}
