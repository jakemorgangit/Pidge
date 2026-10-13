using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using QuickMail.Data;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Password-protected export/import of everything needed to set QuickMail up on another PC:
/// accounts (including passwords), block list, trusted senders, contacts, learned spam data and settings.
/// File format: "QMBK" | version(1) | salt(16) | nonce(12) | tag(16) | AES-256-GCM ciphertext of UTF-8 JSON.
/// The key is derived from the export password with PBKDF2-SHA256.
/// </summary>
public static class SettingsTransfer
{
    private static readonly byte[] Magic = "QMBK"u8.ToArray();
    private const byte Version = 1;
    private const int Iterations = 600_000;

    public class ExportedAccount
    {
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
        public string Password { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    public class Bundle
    {
        public string App { get; set; } = "QuickMail";
        public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
        public string MachineName { get; set; } = Environment.MachineName;
        public List<ExportedAccount> Accounts { get; set; } = [];
        public List<string> BlockedSenders { get; set; } = [];
        public List<string> TrustedSenders { get; set; } = [];
        public List<ContactRecord> Contacts { get; set; } = [];
        public Dictionary<string, int[]> SpamTokens { get; set; } = new();
        public long SpamDocs { get; set; }
        public long HamDocs { get; set; }
        public AppSettings? Settings { get; set; }
        /// <summary>Full shared state (accounts with stable ids, tags, templates...). Newer backups use this.</summary>
        public ConfigSync.SharedState? State { get; set; }
    }

    public record ImportResult(int AccountsAdded, int AccountsUpdated, int BlockedAdded, List<MailAccount> Accounts);

    public static void Export(string path, string password)
    {
        if (password.Length < 8) throw new ArgumentException("Use a password of at least 8 characters.");

        var bundle = new Bundle
        {
            Accounts = MailStore.GetAccounts().Select(a => new ExportedAccount
            {
                DisplayName = a.DisplayName, Email = a.Email, SenderName = a.SenderName, Color = a.Color,
                ImapHost = a.ImapHost, ImapPort = a.ImapPort, ImapSecurity = a.ImapSecurity,
                SmtpHost = a.SmtpHost, SmtpPort = a.SmtpPort, SmtpSecurity = a.SmtpSecurity,
                Username = a.Username, Password = Secrets.Unprotect(a.ProtectedPassword), Signature = a.Signature
            }).ToList(),
            BlockedSenders = MailStore.GetBlocked().Select(b => b.Pattern).ToList(),
            TrustedSenders = MailStore.GetTrusted().ToList(),
            SpamTokens = MailStore.LoadTokens().ToDictionary(t => t.Key, t => new[] { t.Value.spam, t.Value.ham }),
            SpamDocs = MailStore.GetCounter("spamDocs"),
            HamDocs = MailStore.GetCounter("hamDocs"),
            Settings = AppSettings.Current,
            State = ConfigSync.Capture()
        };
        using (var c = Db.Open()) bundle.Contacts = c.Query<ContactRecord>("SELECT * FROM Contacts").ToList();

        var plain = JsonSerializer.SerializeToUtf8Bytes(bundle);
        try
        {
            File.WriteAllBytes(path, Encrypt(plain, password));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static ImportResult Import(string path, string password)
    {
        var plain = Decrypt(File.ReadAllBytes(path), password);
        Bundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<Bundle>(plain) ?? throw new InvalidDataException("The backup is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var existing = MailStore.GetAccounts();
        int added = 0, updated = 0;
        if (bundle.State != null)
        {
            var before = existing.Select(a => a.Email.ToLowerInvariant()).ToHashSet();
            var blockedCount = MailStore.GetBlocked().Count;
            ConfigSync.Apply(bundle.State, merge: true);
            var after = MailStore.GetAccounts();
            added = after.Count(a => !before.Contains(a.Email.ToLowerInvariant()));
            updated = after.Count - added;
            ImportExtras(bundle);
            MailStore.NotifyConfigChanged();
            return new ImportResult(added, updated, MailStore.GetBlocked().Count - blockedCount, after);
        }
        foreach (var e in bundle.Accounts)
        {
            var match = existing.FirstOrDefault(a => a.Email.Equals(e.Email, StringComparison.OrdinalIgnoreCase));
            var account = match ?? new MailAccount();
            account.DisplayName = e.DisplayName; account.Email = e.Email; account.SenderName = e.SenderName;
            account.Color = string.IsNullOrEmpty(e.Color) ? account.Color : e.Color;
            account.ImapHost = e.ImapHost; account.ImapPort = e.ImapPort; account.ImapSecurity = e.ImapSecurity;
            account.SmtpHost = e.SmtpHost; account.SmtpPort = e.SmtpPort; account.SmtpSecurity = e.SmtpSecurity;
            account.Username = e.Username; account.Signature = e.Signature;
            account.ProtectedPassword = Secrets.Protect(e.Password);
            if (match == null) { MailStore.InsertAccount(account); existing.Add(account); added++; }
            else { MailStore.UpdateAccount(account); updated++; }
        }

        var blockedBefore = MailStore.GetBlocked().Count;
        foreach (var b in bundle.BlockedSenders) MailStore.AddBlocked(b);
        foreach (var t in bundle.TrustedSenders) MailStore.AddTrusted(t);
        ImportExtras(bundle);
        SpamEngine.Load();
        return new ImportResult(added, updated, MailStore.GetBlocked().Count - blockedBefore, existing);
    }

    /// <summary>Contacts, spam learning and machine settings, which live outside the shared state.</summary>
    private static void ImportExtras(Bundle bundle)
    {
        using (var c = Db.Open())
        using (var tx = c.BeginTransaction())
        {
            foreach (var ct in bundle.Contacts)
                c.Execute("""
                    INSERT INTO Contacts(Address,Name,SentCount,ReceivedCount,LastSeenUnix) VALUES(@Address,@Name,@SentCount,@ReceivedCount,@LastSeenUnix)
                    ON CONFLICT(Address) DO UPDATE SET SentCount=MAX(SentCount,@SentCount), ReceivedCount=MAX(ReceivedCount,@ReceivedCount),
                        LastSeenUnix=MAX(LastSeenUnix,@LastSeenUnix), Name=CASE WHEN Name='' THEN @Name ELSE Name END
                    """, ct, tx);
            tx.Commit();
        }

        if (bundle.SpamTokens.Count > 0 && MailStore.GetCounter("spamDocs") + MailStore.GetCounter("hamDocs") == 0)
        {
            MailStore.SaveTokens(bundle.SpamTokens.Select(t => new KeyValuePair<string, (int, int)>(t.Key, (t.Value[0], t.Value[1]))));
            MailStore.AddCounter("spamDocs", bundle.SpamDocs);
            MailStore.AddCounter("hamDocs", bundle.HamDocs);
        }

        if (bundle.Settings != null)
        {
            var s = AppSettings.Current;
            s.SyncIntervalMinutes = bundle.Settings.SyncIntervalMinutes;
            s.InitialSyncCount = bundle.Settings.InitialSyncCount;
            s.SpamThreshold = bundle.Settings.SpamThreshold;
            s.MoveSpamToJunk = bundle.Settings.MoveSpamToJunk;
            s.BlockRemoteImages = bundle.Settings.BlockRemoteImages;
            s.DarkReading = bundle.Settings.DarkReading;
            s.CheckForUpdates = bundle.Settings.CheckForUpdates;
            AppSettings.Save();
        }

        SpamEngine.Load();
    }

    internal static byte[] Encrypt(byte[] plain, string password)
    {
        var salt = SaltFor(password);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(password, salt);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Magic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(Version);
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);
        return ms.ToArray();
    }

    private static readonly Dictionary<string, byte[]> Salts = new();

    /// <summary>One random salt per password per run (a fresh nonce is still used for every file).</summary>
    private static byte[] SaltFor(string password)
    {
        lock (Salts)
        {
            var id = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
            if (!Salts.TryGetValue(id, out var salt)) Salts[id] = salt = RandomNumberGenerator.GetBytes(16);
            return salt;
        }
    }

    internal static byte[] Decrypt(byte[] file, string password)
    {
        const int header = 4 + 1 + 16 + 12 + 16;
        if (file.Length < header || !file.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("This isn't a QuickMail backup file.");
        if (file[4] != Version) throw new InvalidDataException("This backup was made by a newer version of QuickMail.");

        var salt = file.AsSpan(5, 16);
        var nonce = file.AsSpan(21, 12);
        var tag = file.AsSpan(33, 16);
        var cipher = file.AsSpan(header);
        var plain = new byte[cipher.Length];
        var key = DeriveKey(password, salt.ToArray());
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("Wrong password, or the file has been changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // PBKDF2 at 600k iterations is deliberately slow; cache keys so live settings sync stays quick.
    private static readonly Dictionary<string, byte[]> KeyCache = new();

    private static byte[] DeriveKey(string password, byte[] salt)
    {
        var cacheKey = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + "|" + Convert.ToBase64String(salt))));
        lock (KeyCache)
        {
            if (KeyCache.TryGetValue(cacheKey, out var k)) return (byte[])k.Clone();
        }
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        lock (KeyCache) KeyCache[cacheKey] = (byte[])key.Clone();
        return key;
    }
}
