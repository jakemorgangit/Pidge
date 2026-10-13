using System.IO;
using System.Text.Json;
using QuickMail.Data;

namespace QuickMail.Services;

public class AppSettings
{
    public int SyncIntervalMinutes { get; set; } = 2;
    public int InitialSyncCount { get; set; } = 300;
    public double SpamThreshold { get; set; } = 0.7;
    public bool MoveSpamToJunk { get; set; }
    public bool BlockRemoteImages { get; set; } = true;
    public bool DarkReading { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 880;
    public bool WindowMaximized { get; set; }

    private static string FilePath => Path.Combine(Db.DataDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json)); } catch { /* non-fatal */ }
    }
}
