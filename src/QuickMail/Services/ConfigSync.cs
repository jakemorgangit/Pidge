using System.IO;
using System.Text.Json;
using Dapper;
using QuickMail.Data;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Keeps settings identical across several PCs through a shared folder (Google Drive, OneDrive, Dropbox, a network share...).
/// Every change (accounts added/edited/removed, block list, tags, templates, settings) is written to one encrypted file
/// in that folder; the other PCs notice the file change and apply it within seconds. Last change wins.
/// The file is encrypted with AES-256-GCM using a sync passphrase entered once on each PC.
/// </summary>
public static class ConfigSync
{
    public const string FileName = "quickmail-shared.qmsync";

    public class SharedAccount
    {
        public string AccountGuid { get; set; } = "";
        public AuthMethod AuthMethod { get; set; }
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string Color { get; set; } = "";
        public string ImapHost { get; set; } = "";
        public int ImapPort { get; set; }
        public int ImapSecurity { get; set; }
        public string SmtpHost { get; set; } = "";
        public int SmtpPort { get; set; }
        public int SmtpSecurity { get; set; }
        public string Username { get; set; } = "";
        /// <summary>Password, or OAuth refresh token.</summary>
        public string Secret { get; set; } = "";
        public string Signature { get; set; } = "";
        public int SortOrder { get; set; }
    }

    public record SharedTag(string TagGuid, string Name, string Color);
    public record SharedTagRule(string TagGuid, string Pattern);
    public record SharedMessageTag(string AccountGuid, string MessageKey, string TagGuid);

    public class SharedSettings
    {
        public int SyncIntervalMinutes { get; set; }
        public int InitialSyncCount { get; set; }
        public double SpamThreshold { get; set; }
        public bool MoveSpamToJunk { get; set; }
        public bool BlockRemoteImages { get; set; }
        public bool DarkReading { get; set; }
        public bool CheckForUpdates { get; set; }
        public int CacheRetentionDays { get; set; }
        public string MicrosoftClientId { get; set; } = "";
    }

    public class SharedState
    {
        public long Revision { get; set; }
        public string MachineId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public DateTime SavedUtc { get; set; }
        public List<SharedAccount> Accounts { get; set; } = [];
        public List<string> BlockedSenders { get; set; } = [];
        public List<string> TrustedSenders { get; set; } = [];
        public List<SharedTag> Tags { get; set; } = [];
        public List<SharedTagRule> TagRules { get; set; } = [];
        public List<SharedMessageTag> MessageTags { get; set; } = [];
        public List<ServerTemplate> Templates { get; set; } = [];
        public SharedSettings? Settings { get; set; }
    }

    // ---------------- runtime ----------------
    private static FileSystemWatcher? _watcher;
    private static Timer? _poll;
    private static Timer? _debounce;
    private static DateTime _lastSeenWrite;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Raised (on a background thread) after settings from another PC were applied.</summary>
    public static event Action? Applied;
    /// <summary>Human-readable status for the settings screen.</summary>
    public static event Action<string>? StatusChanged;
    public static string Status { get; private set; } = "";

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(AppSettings.Current.SharedFolder) && Passphrase().Length > 0;
    public static string SharedFile => Path.Combine(AppSettings.Current.SharedFolder, FileName);

    private static string Passphrase() => Secrets.Unprotect(AppSettings.Current.SharedPassphraseProtected);

    private static void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>Starts watching the shared folder (call once at start-up).</summary>
    public static void Start()
    {
        MailStore.ConfigChanged -= OnLocalChange;
        MailStore.ConfigChanged += OnLocalChange;
        StopWatching();
        if (!IsEnabled) return;

        try
        {
            Directory.CreateDirectory(AppSettings.Current.SharedFolder);
            _watcher = new FileSystemWatcher(AppSettings.Current.SharedFolder, FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (_, _) => _ = PullAsync();
            _watcher.Created += (_, _) => _ = PullAsync();
            _watcher.Renamed += (_, _) => _ = PullAsync();
        }
        catch { /* some cloud folders don't support watching; polling covers it */ }

        // Cloud clients sometimes replace files without a change notification, so poll as well.
        _poll = new Timer(_ => _ = PullAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        SetStatus($"Sharing settings via {AppSettings.Current.SharedFolder}");
    }

    private static void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _poll?.Dispose();
        _poll = null;
    }

    /// <summary>
    /// Turns sharing on. If the folder already holds settings from another PC, this PC's accounts are merged into them
    /// (nothing is deleted); otherwise this PC's settings become the shared copy.
    /// </summary>
    public static async Task<string> EnableAsync(string folder, string passphrase)
    {
        if (passphrase.Length < 8) throw new ArgumentException("Use a sync passphrase of at least 8 characters.");
        var file = Path.Combine(folder, FileName);
        SharedState? remote = null;
        if (File.Exists(file))
        {
            var bytes = await ReadSharedAsync(file);
            remote = JsonSerializer.Deserialize<SharedState>(SettingsTransfer.Decrypt(bytes, passphrase))
                     ?? throw new InvalidDataException("The shared settings file is empty.");
        }

        AppSettings.Current.SharedFolder = folder;
        AppSettings.Current.SharedPassphraseProtected = Secrets.Protect(passphrase);
        AppSettings.Save();

        string result;
        if (remote != null)
        {
            Apply(remote, merge: true);
            result = $"Joined settings shared from {remote.MachineName}. This PC's accounts were added to them.";
        }
        else
        {
            result = "This PC's settings are now shared. Turn on sharing on your other PCs with the same folder and passphrase.";
        }
        await PushAsync();
        Start();
        Applied?.Invoke();
        return result;
    }

    public static void Disable()
    {
        StopWatching();
        AppSettings.Current.SharedFolder = "";
        AppSettings.Current.SharedPassphraseProtected = "";
        AppSettings.Current.SharedRevisionApplied = 0;
        AppSettings.Save();
        SetStatus("Settings are not shared.");
    }

    private static void OnLocalChange()
    {
        if (!IsEnabled) return;
        _debounce?.Dispose();
        _debounce = new Timer(_ => _ = PushAsync(), null, TimeSpan.FromSeconds(1.5), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes this PC's settings to the shared folder.</summary>
    public static async Task PushAsync()
    {
        if (!IsEnabled) return;
        await Gate.WaitAsync();
        try
        {
            var state = Capture();
            state.Revision = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AppSettings.Current.SharedRevisionApplied + 1);
            var bytes = SettingsTransfer.Encrypt(JsonSerializer.SerializeToUtf8Bytes(state), Passphrase());

            var temp = SharedFile + "." + AppSettings.Current.MachineId[..8] + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, SharedFile, overwrite: true);
            _lastSeenWrite = File.GetLastWriteTimeUtc(SharedFile);
            AppSettings.Current.SharedRevisionApplied = state.Revision;
            AppSettings.Save();
            SetStatus($"Shared settings updated {DateTime.Now:HH:mm}");
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't write shared settings: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Applies the shared file if another PC has written a newer version.</summary>
    public static async Task PullAsync()
    {
        if (!IsEnabled || !File.Exists(SharedFile)) return;
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            var written = File.GetLastWriteTimeUtc(SharedFile);
            if (written == _lastSeenWrite) return;
            await Task.Delay(400); // let the cloud client finish writing

            var state = JsonSerializer.Deserialize<SharedState>(SettingsTransfer.Decrypt(await ReadSharedAsync(SharedFile), Passphrase()));
            _lastSeenWrite = written;
            if (state == null || state.Revision <= AppSettings.Current.SharedRevisionApplied) return;

            Apply(state, merge: false);
            AppSettings.Current.SharedRevisionApplied = state.Revision;
            AppSettings.Save();
            SetStatus($"Received settings from {state.MachineName} at {DateTime.Now:HH:mm}");
            Applied?.Invoke();
        }
        catch (InvalidDataException ex)
        {
            SetStatus(ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "The shared settings use a different passphrase. Re-enter it in Settings → Sync."
                : ex.Message);
        }
        catch (IOException)
        {
            // file is mid-write; the next poll will pick it up
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't read shared settings: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch (IOException) when (i < 5)
            {
                await Task.Delay(300);
            }
        }
    }

    // ---------------- capture / apply ----------------
    public static SharedState Capture()
    {
        var accounts = MailStore.GetAccounts();
        var tags = MailStore.GetTags();
        var tagGuid = tags.ToDictionary(t => t.Id, t => t.TagGuid);
        var accountGuid = accounts.ToDictionary(a => a.Id, a => a.AccountGuid);

        List<(long AccountId, string MessageKey, long TagId)> messageTags;
        using (var c = Db.Open())
            messageTags = c.Query<(long, string, long)>("SELECT AccountId, MessageKey, TagId FROM MessageTags").ToList();

        var s = AppSettings.Current;
        return new SharedState
        {
            MachineId = s.MachineId,
            MachineName = Environment.MachineName,
            SavedUtc = DateTime.UtcNow,
            Accounts = accounts.Select(a => new SharedAccount
            {
                AccountGuid = a.AccountGuid, AuthMethod = a.AuthMethod, DisplayName = a.DisplayName, Email = a.Email, SenderName = a.SenderName,
                Color = a.Color, ImapHost = a.ImapHost, ImapPort = a.ImapPort, ImapSecurity = a.ImapSecurity, SmtpHost = a.SmtpHost,
                SmtpPort = a.SmtpPort, SmtpSecurity = a.SmtpSecurity, Username = a.Username, Secret = Secrets.Unprotect(a.ProtectedPassword),
                Signature = a.Signature, SortOrder = a.SortOrder
            }).ToList(),
            BlockedSenders = MailStore.GetBlocked().Select(b => b.Pattern).ToList(),
            TrustedSenders = MailStore.GetTrusted().ToList(),
            Tags = tags.Select(t => new SharedTag(t.TagGuid, t.Name, t.Color)).ToList(),
            TagRules = MailStore.GetTagRules().Where(r => tagGuid.ContainsKey(r.TagId)).Select(r => new SharedTagRule(tagGuid[r.TagId], r.Pattern)).ToList(),
            MessageTags = messageTags.Where(m => tagGuid.ContainsKey(m.TagId) && accountGuid.ContainsKey(m.AccountId))
                .Select(m => new SharedMessageTag(accountGuid[m.AccountId], m.MessageKey, tagGuid[m.TagId])).ToList(),
            Templates = MailStore.GetTemplates(),
            Settings = new SharedSettings
            {
                SyncIntervalMinutes = s.SyncIntervalMinutes, InitialSyncCount = s.InitialSyncCount, SpamThreshold = s.SpamThreshold,
                MoveSpamToJunk = s.MoveSpamToJunk, BlockRemoteImages = s.BlockRemoteImages, DarkReading = s.DarkReading,
                CheckForUpdates = s.CheckForUpdates, CacheRetentionDays = s.CacheRetentionDays, MicrosoftClientId = s.MicrosoftClientId
            }
        };
    }

    /// <summary>
    /// Applies shared settings locally. With <paramref name="merge"/> nothing local is removed (used when joining or importing);
    /// otherwise the shared state replaces local accounts, block list, tags and templates.
    /// </summary>
    public static void Apply(SharedState state, bool merge)
    {
        MailStore.SuppressConfigEvents = true;
        try
        {
            // Accounts
            var local = MailStore.GetAccounts();
            var keep = new HashSet<long>();
            foreach (var shared in state.Accounts)
            {
                var match = local.FirstOrDefault(a => a.AccountGuid == shared.AccountGuid)
                            ?? local.FirstOrDefault(a => a.Email.Equals(shared.Email, StringComparison.OrdinalIgnoreCase));
                var a = match ?? new MailAccount();
                a.AccountGuid = shared.AccountGuid;
                a.AuthMethod = shared.AuthMethod;
                a.DisplayName = shared.DisplayName; a.Email = shared.Email; a.SenderName = shared.SenderName;
                if (!string.IsNullOrEmpty(shared.Color)) a.Color = shared.Color;
                a.ImapHost = shared.ImapHost; a.ImapPort = shared.ImapPort; a.ImapSecurity = shared.ImapSecurity;
                a.SmtpHost = shared.SmtpHost; a.SmtpPort = shared.SmtpPort; a.SmtpSecurity = shared.SmtpSecurity;
                a.Username = shared.Username; a.Signature = shared.Signature;
                if (!string.IsNullOrEmpty(shared.Secret)) a.ProtectedPassword = Secrets.Protect(shared.Secret);
                if (match == null)
                {
                    MailStore.InsertAccount(a);
                    local.Add(a);
                }
                a.SortOrder = shared.SortOrder;
                MailStore.UpdateAccount(a);
                MailEngine.Instance.Register(a);
                keep.Add(a.Id);
            }
            if (!merge)
            {
                foreach (var gone in local.Where(a => !keep.Contains(a.Id)).ToList())
                {
                    MailEngine.Instance.Unregister(gone.Id);
                    MailStore.DeleteAccount(gone.Id);
                }
            }

            // Block list
            var blocked = MailStore.GetBlocked().Select(b => b.Pattern).ToHashSet();
            foreach (var p in state.BlockedSenders.Where(p => !blocked.Contains(p))) MailStore.AddBlocked(p);
            if (!merge)
                foreach (var p in blocked.Where(p => !state.BlockedSenders.Contains(p))) MailStore.RemoveBlocked(p);
            foreach (var t in state.TrustedSenders) MailStore.AddTrusted(t);

            // Tags
            var tags = MailStore.GetTags();
            var tagIds = new Dictionary<string, long>();
            foreach (var st in state.Tags)
            {
                var t = tags.FirstOrDefault(x => x.TagGuid == st.TagGuid) ?? tags.FirstOrDefault(x => x.Name.Equals(st.Name, StringComparison.OrdinalIgnoreCase));
                if (t == null)
                {
                    t = MailStore.CreateTag(st.Name, st.Color);
                    using var c = Db.Open();
                    c.Execute("UPDATE Tags SET TagGuid=@g WHERE Id=@id", new { g = st.TagGuid, id = t.Id });
                }
                else
                {
                    t.Name = st.Name;
                    t.Color = st.Color;
                    MailStore.UpdateTag(t);
                    using var c = Db.Open();
                    c.Execute("UPDATE Tags SET TagGuid=@g WHERE Id=@id", new { g = st.TagGuid, id = t.Id });
                }
                tagIds[st.TagGuid] = t.Id;
            }
            if (!merge)
                foreach (var t in tags.Where(t => !tagIds.ContainsValue(t.Id))) MailStore.DeleteTag(t.Id);

            var rules = MailStore.GetTagRules();
            foreach (var r in state.TagRules.Where(r => tagIds.ContainsKey(r.TagGuid))) MailStore.AddTagRule(tagIds[r.TagGuid], r.Pattern);
            if (!merge)
                foreach (var r in rules.Where(r => !state.TagRules.Any(s => tagIds.GetValueOrDefault(s.TagGuid) == r.TagId && s.Pattern == r.Pattern)))
                    MailStore.RemoveTagRule(r.TagId, r.Pattern);

            var accountsByGuid = MailStore.GetAccounts().ToDictionary(a => a.AccountGuid, a => a.Id);
            foreach (var mt in state.MessageTags)
                if (accountsByGuid.TryGetValue(mt.AccountGuid, out var aid) && tagIds.TryGetValue(mt.TagGuid, out var tid))
                    MailStore.AddMessageTag(aid, mt.MessageKey, tid);

            // Server templates
            var templates = MailStore.GetTemplates();
            foreach (var t in state.Templates) MailStore.SaveTemplate(new ServerTemplate
            {
                Name = t.Name, ImapHost = t.ImapHost, ImapPort = t.ImapPort, ImapSecurity = t.ImapSecurity,
                SmtpHost = t.SmtpHost, SmtpPort = t.SmtpPort, SmtpSecurity = t.SmtpSecurity
            });
            if (!merge)
                foreach (var t in templates.Where(t => !state.Templates.Any(s => s.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))))
                    MailStore.DeleteTemplate(t.Id);

            // Settings
            if (state.Settings is { } ss)
            {
                var s = AppSettings.Current;
                s.SyncIntervalMinutes = ss.SyncIntervalMinutes; s.InitialSyncCount = ss.InitialSyncCount; s.SpamThreshold = ss.SpamThreshold;
                s.MoveSpamToJunk = ss.MoveSpamToJunk; s.BlockRemoteImages = ss.BlockRemoteImages; s.DarkReading = ss.DarkReading;
                s.CheckForUpdates = ss.CheckForUpdates; s.CacheRetentionDays = ss.CacheRetentionDays; s.MicrosoftClientId = ss.MicrosoftClientId;
                AppSettings.Save();
            }
        }
        finally
        {
            MailStore.SuppressConfigEvents = false;
        }
        SpamEngine.RefreshLists();
    }
}
