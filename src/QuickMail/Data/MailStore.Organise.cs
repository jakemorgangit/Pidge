using Dapper;
using QuickMail.Models;

namespace QuickMail.Data;

/// <summary>Folders, tags, server templates.</summary>
public static partial class MailStore
{
    // ---------------- Folders ----------------
    public static List<FolderInfo> GetFolders(long accountId)
    {
        using var c = Db.Open();
        return c.Query<FolderInfo>("SELECT * FROM Folders WHERE AccountId=@accountId ORDER BY SortKey", new { accountId }).ToList();
    }

    public static void ReplaceFolders(long accountId, IEnumerable<FolderInfo> folders)
    {
        using var c = Db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM Folders WHERE AccountId=@accountId", new { accountId }, tx);
        c.Execute("""
            INSERT INTO Folders(AccountId,Path,Name,Depth,Unread,Total,Selectable,SortKey)
            VALUES(@AccountId,@Path,@Name,@Depth,@Unread,@Total,@Selectable,@SortKey)
            """, folders, tx);
        // Forget cached mail for folders that no longer exist on the server.
        c.Execute("DELETE FROM Messages WHERE AccountId=@accountId AND Folder=6 AND FolderPath NOT IN (SELECT Path FROM Folders WHERE AccountId=@accountId)",
            new { accountId }, tx);
        tx.Commit();
    }

    public static void SetFolderUnread(long accountId, string path, int unread, int total) =>
        Exec("UPDATE Folders SET Unread=@unread, Total=@total WHERE AccountId=@accountId AND Path=@path", new { accountId, path, unread, total });

    public static (int unread, int total) CountFolder(long accountId, FolderKind folder, string path)
    {
        using var c = Db.Open();
        var r = c.QuerySingle<(long Unread, long Total)>(
            $"SELECT COALESCE(SUM(CASE WHEN IsRead=0 THEN 1 ELSE 0 END),0), COUNT(*) FROM Messages WHERE {Where}", new { accountId, folder, path });
        return ((int)r.Unread, (int)r.Total);
    }

    // ---------------- Tags ----------------
    public static List<TagInfo> GetTags()
    {
        using var c = Db.Open();
        return c.Query<TagInfo>("""
            SELECT t.*, (SELECT COUNT(*) FROM MessageTags mt WHERE mt.TagId=t.Id) AS Count
            FROM Tags t ORDER BY t.Name COLLATE NOCASE
            """).ToList();
    }

    public static TagInfo CreateTag(string name, string color)
    {
        var tag = new TagInfo { Name = name.Trim(), Color = color };
        using (var c = Db.Open())
            tag.Id = c.ExecuteScalar<long>("INSERT INTO Tags(TagGuid,Name,Color) VALUES(@TagGuid,@Name,@Color); SELECT last_insert_rowid();", tag);
        Changed();
        return tag;
    }

    public static void UpdateTag(TagInfo tag)
    {
        Exec("UPDATE Tags SET Name=@Name, Color=@Color WHERE Id=@Id", tag);
        Changed();
    }

    public static void DeleteTag(long tagId)
    {
        Exec("DELETE FROM MessageTags WHERE TagId=@tagId; DELETE FROM TagRules WHERE TagId=@tagId; DELETE FROM Tags WHERE Id=@tagId;", new { tagId });
        Changed();
    }

    /// <summary>Tag ids per message key for one account (used to draw tag chips).</summary>
    public static Dictionary<(long, string), List<long>> TagsForMessages(IEnumerable<MessageRecord> messages)
    {
        var list = messages.ToList();
        if (list.Count == 0) return new();
        using var c = Db.Open();
        var accountIds = list.Select(m => m.AccountId).Distinct().ToList();
        return c.Query<(long AccountId, string MessageKey, long TagId)>(
                "SELECT AccountId, MessageKey, TagId FROM MessageTags WHERE AccountId IN @accountIds", new { accountIds })
            .GroupBy(r => (r.AccountId, r.MessageKey))
            .ToDictionary(g => g.Key, g => g.Select(r => r.TagId).ToList());
    }

    public static void AddMessageTag(long accountId, string messageKey, long tagId) =>
        Exec("INSERT OR IGNORE INTO MessageTags(AccountId,MessageKey,TagId) VALUES(@accountId,@messageKey,@tagId)", new { accountId, messageKey, tagId });

    public static void RemoveMessageTag(long accountId, string messageKey, long tagId) =>
        Exec("DELETE FROM MessageTags WHERE AccountId=@accountId AND MessageKey=@messageKey AND TagId=@tagId", new { accountId, messageKey, tagId });

    public static List<TagRule> GetTagRules()
    {
        using var c = Db.Open();
        return c.Query<TagRule>("SELECT * FROM TagRules ORDER BY TagId, Pattern").ToList();
    }

    public static void AddTagRule(long tagId, string pattern)
    {
        Exec("INSERT OR IGNORE INTO TagRules(TagId,Pattern,CreatedUnix) VALUES(@tagId,@p,@n)", new { tagId, p = pattern.ToLowerInvariant(), n = Now });
        Changed();
    }

    public static void RemoveTagRule(long tagId, string pattern)
    {
        Exec("DELETE FROM TagRules WHERE TagId=@tagId AND Pattern=@pattern", new { tagId, pattern });
        Changed();
    }

    /// <summary>Distinct senders that carry a tag (used to learn domain-wide rules).</summary>
    public static List<string> TaggedSenders(long tagId)
    {
        using var c = Db.Open();
        return c.Query<string>("""
            SELECT DISTINCT lower(m.FromAddress) FROM Messages m JOIN MessageTags t ON t.AccountId=m.AccountId AND t.MessageKey =
                CASE WHEN m.MessageId<>'' THEN m.MessageId ELSE 'uid:' || m.Folder || ':' || m.FolderPath || ':' || m.Uid END
            WHERE t.TagId=@tagId AND m.FromAddress<>''
            """, new { tagId }).ToList();
    }

    /// <summary>All cached messages (any folder except spam/bin) from an address or "@domain".</summary>
    public static List<MessageRecord> MessagesMatching(string pattern)
    {
        using var c = Db.Open();
        var sql = pattern.StartsWith('@')
            ? "SELECT * FROM Messages WHERE Folder NOT IN (4,5) AND lower(FromAddress) LIKE @like"
            : "SELECT * FROM Messages WHERE Folder NOT IN (4,5) AND lower(FromAddress)=@pattern";
        return c.Query<MessageRecord>(sql, new { pattern, like = "%" + pattern }).ToList();
    }

    // ---------------- Server templates ----------------
    public static List<ServerTemplate> GetTemplates()
    {
        using var c = Db.Open();
        return c.Query<ServerTemplate>("SELECT * FROM ServerTemplates ORDER BY Name COLLATE NOCASE").ToList();
    }

    public static void SaveTemplate(ServerTemplate t)
    {
        using (var c = Db.Open())
        {
            var existing = c.ExecuteScalar<long?>("SELECT Id FROM ServerTemplates WHERE lower(Name)=lower(@Name)", t);
            if (existing.HasValue)
            {
                t.Id = existing.Value;
                c.Execute("""
                    UPDATE ServerTemplates SET ImapHost=@ImapHost,ImapPort=@ImapPort,ImapSecurity=@ImapSecurity,
                    SmtpHost=@SmtpHost,SmtpPort=@SmtpPort,SmtpSecurity=@SmtpSecurity WHERE Id=@Id
                    """, t);
            }
            else
            {
                t.Id = c.ExecuteScalar<long>("""
                    INSERT INTO ServerTemplates(Name,ImapHost,ImapPort,ImapSecurity,SmtpHost,SmtpPort,SmtpSecurity)
                    VALUES(@Name,@ImapHost,@ImapPort,@ImapSecurity,@SmtpHost,@SmtpPort,@SmtpSecurity); SELECT last_insert_rowid();
                    """, t);
            }
        }
        Changed();
    }

    public static void DeleteTemplate(long id)
    {
        Exec("DELETE FROM ServerTemplates WHERE Id=@id", new { id });
        Changed();
    }

    // ---------------- Storage ----------------
    public static void Vacuum() => Exec("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;", null);
}
