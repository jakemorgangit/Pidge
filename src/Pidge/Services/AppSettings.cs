using System.IO;
using System.Text.Json;
using Pidge.Data;

namespace Pidge.Services;

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

    /// <summary>Interface zoom (1.0 = 100%). Ctrl + mouse wheel changes it.</summary>
    public double UiScale { get; set; } = 1.0;
    /// <summary>Remove downloaded message bodies not opened for this many days (0 = keep). Headers always stay.</summary>
    public int CacheRetentionDays { get; set; }
    public DateTime LastCompactUtc { get; set; }
    /// <summary>Overrides the built-in Microsoft Entra application (client) ID used for "Sign in with Microsoft".</summary>
    public string MicrosoftClientId { get; set; } = "";

    // ---- Notifications (per PC) ----
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotifySound { get; set; } = true;
    /// <summary>Only notify for mail from people (skip newsletters, promotions, notifications and social).</summary>
    public bool NotifyPeopleOnly { get; set; }
    /// <summary>Accounts (by AccountGuid) that never notify.</summary>
    public List<string> MutedAccounts { get; set; } = [];
    /// <summary>Notifications are paused until this time (UTC). DateTime.MaxValue = until turned back on.</summary>
    public DateTime? NotificationsPausedUntilUtc { get; set; }

    // ---- Shared settings between PCs (machine-local; never synced themselves) ----
    public string SharedFolder { get; set; } = "";
    public string SharedPassphraseProtected { get; set; } = "";
    public long SharedRevisionApplied { get; set; }
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");

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
