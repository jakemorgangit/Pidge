using System.IO;
using Dapper;
using QuickMail.Models;

namespace QuickMail.Data;

public enum MessageFilter { All, Unread, Flagged, Attachments, Spam }

public class SenderStat
{
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public int Total { get; set; }
    public int ReadCount { get; set; }
    public int SpamCount { get; set; }
    public double ReadRate => Total == 0 ? 0 : (double)ReadCount / Total;
}

public class DayCount
{
    public string Day { get; set; } = "";
    public int Count { get; set; }
}

public class CategoryCount
{
    public MailCategory Category { get; set; }
    public int Count { get; set; }
}

public class AccountStat
{
    public long AccountId { get; set; }
    public int Total { get; set; }
    public int Unread { get; set; }
    public int Today { get; set; }
    public int Spam { get; set; }
    public int Flagged { get; set; }
}

/// <summary>All SQLite access lives here.</summary>
public static partial class MailStore
{
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------------- Accounts ----------------
    public static List<MailAccount> GetAccounts()
    {
        using var c = Db.Open();
        return c.Query<MailAccount>("SELECT * FROM Accounts ORDER BY SortOrder, Id").ToList();
    }

    public static long InsertAccount(MailAccount a)
    {
        using var c = Db.Open();
        a.SortOrder = c.ExecuteScalar<int>("SELECT COALESCE(MAX(SortOrder),0)+1 FROM Accounts");
        if (string.IsNullOrEmpty(a.AccountGuid)) a.AccountGuid = Guid.NewGuid().ToString("N");
        a.Id = c.ExecuteScalar<long>("""
            INSERT INTO Accounts(AccountGuid,AuthMethod,DisplayName,Email,SenderName,Color,ImapHost,ImapPort,ImapSecurity,SmtpHost,SmtpPort,SmtpSecurity,Username,ProtectedPassword,Signature,SortOrder)
            VALUES(@AccountGuid,@AuthMethod,@DisplayName,@Email,@SenderName,@Color,@ImapHost,@ImapPort,@ImapSecurity,@SmtpHost,@SmtpPort,@SmtpSecurity,@Username,@ProtectedPassword,@Signature,@SortOrder);
            SELECT last_insert_rowid();
            """, a);
        Changed();
        return a.Id;
    }

    public static void UpdateAccount(MailAccount a)
    {
        using var c = Db.Open();
        c.Execute("""
            UPDATE Accounts SET AuthMethod=@AuthMethod,DisplayName=@DisplayName,Email=@Email,SenderName=@SenderName,Color=@Color,ImapHost=@ImapHost,ImapPort=@ImapPort,
            ImapSecurity=@ImapSecurity,SmtpHost=@SmtpHost,SmtpPort=@SmtpPort,SmtpSecurity=@SmtpSecurity,Username=@Username,
            ProtectedPassword=@ProtectedPassword,Signature=@Signature,SortOrder=@SortOrder WHERE Id=@Id
            """, a);
        Changed();
    }

    /// <summary>Raised whenever shared configuration changes (accounts, block list, tags, templates).</summary>
    public static event Action? ConfigChanged;

    /// <summary>Suppresses <see cref="ConfigChanged"/> while applying settings received from another PC.</summary>
    public static bool SuppressConfigEvents { get; set; }

    private static void Changed()
    {
        if (!SuppressConfigEvents) ConfigChanged?.Invoke();
    }

    public static void NotifyConfigChanged() => Changed();

    public static void SetLastSync(long accountId)
    {
        using var c = Db.Open();
        c.Execute("UPDATE Accounts SET LastSyncUnix=@n WHERE Id=@accountId", new { n = Now, accountId });
    }

    public static void SetSortOrders(IEnumerable<MailAccount> ordered)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        int i = 0;
        foreach (var a in ordered)
        {
            a.SortOrder = i++;
            c.Execute("UPDATE Accounts SET SortOrder=@SortOrder WHERE Id=@Id", a, tx);
        }
        tx.Commit();
        Changed();
    }

    public static void DeleteAccount(long id)
    {
        using (var c = Db.Open())
        {
            c.Execute("""
                DELETE FROM Messages WHERE AccountId=@id; DELETE FROM Folders WHERE AccountId=@id;
                DELETE FROM MessageTags WHERE AccountId=@id; DELETE FROM Accounts WHERE Id=@id;
                """, new { id });
        }
        try { Directory.Delete(Path.Combine(Db.CacheDir, id.ToString()), true); } catch { /* cache is optional */ }
        Changed();
    }

    // ---------------- Messages: sync helpers ----------------
    // `path` is only used for FolderKind.Custom folders and is "" for the special folders.
    private const string Where = "AccountId=@accountId AND Folder=@folder AND FolderPath=@path";

    public static long GetMaxUid(long accountId, FolderKind folder, string path = "")
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long?>($"SELECT MAX(Uid) FROM Messages WHERE {Where}", new { accountId, folder, path }) ?? 0;
    }

    public static long GetMinUid(long accountId, FolderKind folder, string path = "")
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long?>($"SELECT MIN(Uid) FROM Messages WHERE {Where}", new { accountId, folder, path }) ?? 0;
    }

    public static long GetUidValidity(long accountId, FolderKind folder, string path = "")
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long?>($"SELECT UidValidity FROM Messages WHERE {Where} LIMIT 1", new { accountId, folder, path }) ?? 0;
    }

    public static void ClearFolder(long accountId, FolderKind folder, string path = "")
    {
        using var c = Db.Open();
        c.Execute($"DELETE FROM Messages WHERE {Where}", new { accountId, folder, path });
    }

    public static void InsertMessages(IEnumerable<MessageRecord> records)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            INSERT OR IGNORE INTO Messages(AccountId,Folder,FolderPath,Uid,UidValidity,MessageId,InReplyTo,Refs,Subject,FromName,FromAddress,ReplyTo,ToList,CcList,
                DateUnix,Preview,IsRead,IsFlagged,IsAnswered,HasAttachments,IsSpam,SpamScore,SpamReasons,Category,ListUnsubscribe,Size)
            VALUES(@AccountId,@Folder,@FolderPath,@Uid,@UidValidity,@MessageId,@InReplyTo,@Refs,@Subject,@FromName,@FromAddress,@ReplyTo,@ToList,@CcList,
                @DateUnix,@Preview,@IsRead,@IsFlagged,@IsAnswered,@HasAttachments,@IsSpam,@SpamScore,@SpamReasons,@Category,@ListUnsubscribe,@Size)
            """, records, tx);
        tx.Commit();
    }

    public static Dictionary<long, (bool read, bool flagged, bool answered)> GetLocalFlags(long accountId, FolderKind folder, string path, long fromUid, long toUid)
    {
        using var c = Db.Open();
        return c.Query<(long Uid, bool IsRead, bool IsFlagged, bool IsAnswered)>(
                $"SELECT Uid, IsRead, IsFlagged, IsAnswered FROM Messages WHERE {Where} AND Uid>=@fromUid AND Uid<=@toUid",
                new { accountId, folder, path, fromUid, toUid })
            .ToDictionary(r => r.Uid, r => (r.IsRead, r.IsFlagged, r.IsAnswered));
    }

    public static void ApplyFlagChanges(long accountId, FolderKind folder, string path,
        IEnumerable<(long uid, bool read, bool flagged, bool answered)> changes, IEnumerable<long> vanished)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var ch in changes)
            c.Execute($"UPDATE Messages SET IsRead=@read, IsFlagged=@flagged, IsAnswered=@answered WHERE {Where} AND Uid=@uid",
                new { ch.read, ch.flagged, ch.answered, accountId, folder, path, ch.uid }, tx);
        foreach (var uid in vanished)
            c.Execute($"DELETE FROM Messages WHERE {Where} AND Uid=@uid", new { accountId, folder, path, uid }, tx);
        tx.Commit();
    }

    // ---------------- Messages: UI ----------------
    public static List<MessageRecord> QueryMessages(long? accountId, FolderKind folder, MessageFilter filter, string? search, int limit, string path = "")
    {
        var sql = new System.Text.StringBuilder("SELECT * FROM Messages WHERE Folder=@folder AND FolderPath=@path");
        if (accountId.HasValue) sql.Append(" AND AccountId=@accountId");
        if (folder != FolderKind.Junk)
            sql.Append(filter == MessageFilter.Spam ? " AND IsSpam=1" : " AND IsSpam=0");
        switch (filter)
        {
            case MessageFilter.Unread: sql.Append(" AND IsRead=0"); break;
            case MessageFilter.Flagged: sql.Append(" AND IsFlagged=1"); break;
            case MessageFilter.Attachments: sql.Append(" AND HasAttachments=1"); break;
        }
        if (!string.IsNullOrWhiteSpace(search))
            sql.Append(" AND (Subject LIKE @q OR FromName LIKE @q OR FromAddress LIKE @q OR Preview LIKE @q OR ToList LIKE @q)");
        sql.Append(" ORDER BY DateUnix DESC LIMIT @limit");

        using var c = Db.Open();
        return c.Query<MessageRecord>(sql.ToString(), new { folder, path, accountId, q = $"%{search?.Trim()}%", limit }).ToList();
    }

    /// <summary>Everything carrying a tag, across all accounts and folders (except spam and bin).</summary>
    public static List<MessageRecord> QueryTagged(long tagId, MessageFilter filter, string? search, int limit)
    {
        var sql = new System.Text.StringBuilder("""
            SELECT m.* FROM Messages m JOIN MessageTags t ON t.AccountId=m.AccountId AND t.MessageKey =
                CASE WHEN m.MessageId<>'' THEN m.MessageId ELSE 'uid:' || m.Folder || ':' || m.FolderPath || ':' || m.Uid END
            WHERE t.TagId=@tagId AND m.Folder NOT IN (4,5)
            """);
        switch (filter)
        {
            case MessageFilter.Unread: sql.Append(" AND m.IsRead=0"); break;
            case MessageFilter.Flagged: sql.Append(" AND m.IsFlagged=1"); break;
            case MessageFilter.Attachments: sql.Append(" AND m.HasAttachments=1"); break;
        }
        if (!string.IsNullOrWhiteSpace(search))
            sql.Append(" AND (m.Subject LIKE @q OR m.FromName LIKE @q OR m.FromAddress LIKE @q OR m.Preview LIKE @q)");
        sql.Append(" GROUP BY m.Id ORDER BY m.DateUnix DESC LIMIT @limit");
        using var c = Db.Open();
        return c.Query<MessageRecord>(sql.ToString(), new { tagId, q = $"%{search?.Trim()}%", limit }).ToList();
    }

    public static MessageRecord? GetMessage(long id)
    {
        using var c = Db.Open();
        return c.QuerySingleOrDefault<MessageRecord>("SELECT * FROM Messages WHERE Id=@id", new { id });
    }

    public static void SetRead(long id, bool value) => Exec("UPDATE Messages SET IsRead=@value WHERE Id=@id", new { id, value });
    public static void SetFlagged(long id, bool value) => Exec("UPDATE Messages SET IsFlagged=@value WHERE Id=@id", new { id, value });
    public static void SetAnswered(long id) => Exec("UPDATE Messages SET IsAnswered=1 WHERE Id=@id", new { id });
    public static void SetSpam(long id, bool value, string reasons) =>
        Exec("UPDATE Messages SET IsSpam=@value, SpamReasons=@reasons, SpamScore=CASE WHEN @value THEN MAX(SpamScore,0.99) ELSE 0 END WHERE Id=@id",
            new { id, value, reasons });
    public static void DeleteMessage(long id) => Exec("DELETE FROM Messages WHERE Id=@id", new { id });

    public static int CountSpam(long? accountId)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Messages WHERE Folder=0 AND IsSpam=1" + (accountId.HasValue ? " AND AccountId=@accountId" : ""),
            new { accountId });
    }

    public static Dictionary<long, int> UnreadByAccount()
    {
        using var c = Db.Open();
        return c.Query<(long AccountId, int N)>("SELECT AccountId, COUNT(*) FROM Messages WHERE Folder=0 AND IsSpam=0 AND IsRead=0 GROUP BY AccountId")
            .ToDictionary(r => r.AccountId, r => r.N);
    }

    public static List<MessageRecord> MessagesFrom(string address, FolderKind folder)
    {
        using var c = Db.Open();
        return c.Query<MessageRecord>("SELECT * FROM Messages WHERE Folder=@folder AND FromAddress=@address COLLATE NOCASE", new { folder, address }).ToList();
    }

    public static List<MessageRecord> MessagesFromDomain(string domain, FolderKind folder)
    {
        using var c = Db.Open();
        return c.Query<MessageRecord>("SELECT * FROM Messages WHERE Folder=@folder AND FromAddress LIKE @pattern COLLATE NOCASE",
            new { folder, pattern = "%@" + domain }).ToList();
    }

    // ---------------- Dashboard ----------------
    public static List<AccountStat> AccountStats()
    {
        var todayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        using var c = Db.Open();
        return c.Query<AccountStat>("""
            SELECT AccountId,
                   SUM(CASE WHEN IsSpam=0 THEN 1 ELSE 0 END) AS Total,
                   SUM(CASE WHEN IsSpam=0 AND IsRead=0 THEN 1 ELSE 0 END) AS Unread,
                   SUM(CASE WHEN IsSpam=0 AND DateUnix>=@todayStart THEN 1 ELSE 0 END) AS Today,
                   SUM(CASE WHEN IsSpam=1 THEN 1 ELSE 0 END) AS Spam,
                   SUM(CASE WHEN IsFlagged=1 THEN 1 ELSE 0 END) AS Flagged
            FROM Messages WHERE Folder=0 GROUP BY AccountId
            """, new { todayStart }).ToList();
    }

    public static List<DayCount> DailyVolume(int days)
    {
        var from = new DateTimeOffset(DateTime.Today.AddDays(-(days - 1))).ToUnixTimeSeconds();
        using var c = Db.Open();
        return c.Query<DayCount>("""
            SELECT date(DateUnix,'unixepoch','localtime') AS Day, COUNT(*) AS Count
            FROM Messages WHERE Folder=0 AND DateUnix>=@from GROUP BY Day ORDER BY Day
            """, new { from }).ToList();
    }

    public static List<CategoryCount> Categories(int days)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        using var c = Db.Open();
        return c.Query<CategoryCount>("SELECT Category, COUNT(*) AS Count FROM Messages WHERE Folder=0 AND IsSpam=0 AND DateUnix>=@from GROUP BY Category",
            new { from }).ToList();
    }

    public static List<SenderStat> TopSenders(int days, int limit)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        using var c = Db.Open();
        return c.Query<SenderStat>("""
            SELECT FromAddress, MAX(FromName) AS FromName, COUNT(*) AS Total,
                   SUM(IsRead) AS ReadCount, SUM(IsSpam) AS SpamCount
            FROM Messages WHERE Folder=0 AND DateUnix>=@from AND FromAddress<>''
            GROUP BY lower(FromAddress) ORDER BY Total DESC LIMIT @limit
            """, new { from, limit }).ToList();
    }

    /// <summary>Senders that mail you often but whose messages you almost never open.</summary>
    public static List<SenderStat> NoisySenders(int limit)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeSeconds();
        using var c = Db.Open();
        return c.Query<SenderStat>("""
            SELECT m.FromAddress, MAX(m.FromName) AS FromName, COUNT(*) AS Total, SUM(m.IsRead) AS ReadCount, SUM(m.IsSpam) AS SpamCount
            FROM Messages m
            LEFT JOIN Contacts ct ON ct.Address = lower(m.FromAddress)
            LEFT JOIN BlockedSenders b ON b.Pattern = lower(m.FromAddress)
            WHERE m.Folder=0 AND m.DateUnix>=@from AND m.FromAddress<>'' AND COALESCE(ct.SentCount,0)=0 AND b.Pattern IS NULL
            GROUP BY lower(m.FromAddress)
            HAVING COUNT(*)>=4 AND (CAST(SUM(m.IsRead) AS REAL)/COUNT(*))<=0.15
            ORDER BY Total DESC LIMIT @limit
            """, new { from, limit }).ToList();
    }

    /// <summary>Unread mail from people you have written to before.</summary>
    public static List<MessageRecord> NeedsAttention(int limit)
    {
        using var c = Db.Open();
        return c.Query<MessageRecord>("""
            SELECT m.* FROM Messages m JOIN Contacts ct ON ct.Address = lower(m.FromAddress)
            WHERE m.Folder=0 AND m.IsSpam=0 AND (m.IsRead=0 OR m.IsFlagged=1) AND ct.SentCount>0
            ORDER BY m.DateUnix DESC LIMIT @limit
            """, new { limit }).ToList();
    }

    // ---------------- Block list / trusted ----------------
    public static List<(string Pattern, long CreatedUnix, int Hits)> GetBlocked()
    {
        using var c = Db.Open();
        return c.Query<(string, long, int)>("SELECT Pattern, CreatedUnix, Hits FROM BlockedSenders ORDER BY CreatedUnix DESC").ToList();
    }

    public static void AddBlocked(string pattern) =>
        Exec("INSERT OR IGNORE INTO BlockedSenders(Pattern, CreatedUnix) VALUES(@p, @n)", new { p = pattern.Trim().ToLowerInvariant(), n = Now });

    public static void RemoveBlocked(string pattern) => Exec("DELETE FROM BlockedSenders WHERE Pattern=@pattern", new { pattern });
    public static void BlockedHit(string pattern) => Exec("UPDATE BlockedSenders SET Hits=Hits+1 WHERE Pattern=@pattern", new { pattern });

    public static HashSet<string> GetTrusted()
    {
        using var c = Db.Open();
        return c.Query<string>("SELECT Address FROM TrustedSenders").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void AddTrusted(string address)
    {
        Exec("INSERT OR IGNORE INTO TrustedSenders(Address) VALUES(@a)", new { a = address.ToLowerInvariant() });
        Changed();
    }

    // ---------------- Contacts ----------------
    public static void RecordSent(IEnumerable<(string address, string name)> recipients)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var (address, name) in recipients)
            c.Execute("""
                INSERT INTO Contacts(Address, Name, SentCount, LastSeenUnix) VALUES(@a, @name, 1, @n)
                ON CONFLICT(Address) DO UPDATE SET SentCount=SentCount+1, LastSeenUnix=@n, Name=CASE WHEN @name<>'' THEN @name ELSE Name END
                """, new { a = address.ToLowerInvariant(), name, n = Now }, tx);
        tx.Commit();
    }

    public static void RecordReceived(IEnumerable<MessageRecord> messages)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var m in messages.Where(m => m.FromAddress != ""))
            c.Execute("""
                INSERT INTO Contacts(Address, Name, ReceivedCount, LastSeenUnix) VALUES(@a, @name, 1, @n)
                ON CONFLICT(Address) DO UPDATE SET ReceivedCount=ReceivedCount+1, LastSeenUnix=MAX(LastSeenUnix,@n),
                    Name=CASE WHEN Name='' THEN @name ELSE Name END
                """, new { a = m.FromAddress.ToLowerInvariant(), name = m.FromName, n = m.DateUnix }, tx);
        tx.Commit();
    }

    public static HashSet<string> KnownCorrespondents()
    {
        using var c = Db.Open();
        return c.Query<string>("SELECT Address FROM Contacts WHERE SentCount>0").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static List<ContactRecord> SearchContacts(string text, int limit)
    {
        using var c = Db.Open();
        return c.Query<ContactRecord>("""
            SELECT * FROM Contacts WHERE (Address LIKE @q OR Name LIKE @q)
            ORDER BY SentCount DESC, ReceivedCount DESC, LastSeenUnix DESC LIMIT @limit
            """, new { q = $"%{text}%", limit }).ToList();
    }

    // ---------------- Spam tokens / counters ----------------
    public static Dictionary<string, (int spam, int ham)> LoadTokens()
    {
        using var c = Db.Open();
        return c.Query<(string Token, int Spam, int Ham)>("SELECT Token, Spam, Ham FROM SpamTokens")
            .ToDictionary(t => t.Token, t => (t.Spam, t.Ham));
    }

    public static void SaveTokens(IEnumerable<KeyValuePair<string, (int spam, int ham)>> tokens)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        foreach (var t in tokens)
            c.Execute("INSERT INTO SpamTokens(Token,Spam,Ham) VALUES(@k,@s,@h) ON CONFLICT(Token) DO UPDATE SET Spam=@s, Ham=@h",
                new { k = t.Key, s = t.Value.spam, h = t.Value.ham }, tx);
        tx.Commit();
    }

    public static void ResetTokens()
    {
        Exec("DELETE FROM SpamTokens; DELETE FROM Counters WHERE Key IN ('spamDocs','hamDocs');", null);
    }

    public static long GetCounter(string key)
    {
        using var c = Db.Open();
        return c.ExecuteScalar<long?>("SELECT Value FROM Counters WHERE Key=@key", new { key }) ?? 0;
    }

    public static void AddCounter(string key, long delta) =>
        Exec("INSERT INTO Counters(Key,Value) VALUES(@key,@delta) ON CONFLICT(Key) DO UPDATE SET Value=Value+@delta", new { key, delta });

    private static void Exec(string sql, object? args)
    {
        using var c = Db.Open();
        c.Execute(sql, args);
    }
}
