using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;

namespace QuickMail.Data;

public static class Db
{
    /// <summary>Set QUICKMAIL_DATA to use a different data folder (handy for testing).</summary>
    public static readonly string DataDir = Environment.GetEnvironmentVariable("QUICKMAIL_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickMail");

    public static string CacheDir => Path.Combine(DataDir, "cache");

    private static string _connectionString = "";

    public static SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public static void Initialize()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CacheDir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataDir, "quickmail.db"),
            Pooling = true
        }.ToString();

        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        c.Execute(Schema);
        Migrate(c);
    }

    /// <summary>Schema upgrades, tracked with PRAGMA user_version.</summary>
    private static void Migrate(SqliteConnection c)
    {
        var version = c.ExecuteScalar<long>("PRAGMA user_version");

        if (version < 2)
        {
            using var tx = c.BeginTransaction();
            // Accounts gain a stable id (for syncing settings between PCs) and an auth method.
            if (!HasColumn(c, "Accounts", "AccountGuid"))
                c.Execute("ALTER TABLE Accounts ADD COLUMN AccountGuid TEXT NOT NULL DEFAULT ''", transaction: tx);
            if (!HasColumn(c, "Accounts", "AuthMethod"))
                c.Execute("ALTER TABLE Accounts ADD COLUMN AuthMethod INTEGER NOT NULL DEFAULT 0", transaction: tx);
            foreach (var id in c.Query<long>("SELECT Id FROM Accounts WHERE AccountGuid=''", transaction: tx).ToList())
                c.Execute("UPDATE Accounts SET AccountGuid=@g WHERE Id=@id", new { g = Guid.NewGuid().ToString("N"), id }, tx);

            // Messages gain FolderPath so any IMAP folder can be cached, not just the special ones.
            if (!HasColumn(c, "Messages", "FolderPath"))
            {
                c.Execute("""
                    CREATE TABLE Messages_v2(
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        AccountId INTEGER NOT NULL, Folder INTEGER NOT NULL, FolderPath TEXT NOT NULL DEFAULT '', Uid INTEGER NOT NULL, UidValidity INTEGER NOT NULL DEFAULT 0,
                        MessageId TEXT NOT NULL DEFAULT '', InReplyTo TEXT NOT NULL DEFAULT '', Refs TEXT NOT NULL DEFAULT '',
                        Subject TEXT NOT NULL DEFAULT '', FromName TEXT NOT NULL DEFAULT '', FromAddress TEXT NOT NULL DEFAULT '',
                        ReplyTo TEXT NOT NULL DEFAULT '', ToList TEXT NOT NULL DEFAULT '', CcList TEXT NOT NULL DEFAULT '',
                        DateUnix INTEGER NOT NULL DEFAULT 0, Preview TEXT NOT NULL DEFAULT '',
                        IsRead INTEGER NOT NULL DEFAULT 0, IsFlagged INTEGER NOT NULL DEFAULT 0, IsAnswered INTEGER NOT NULL DEFAULT 0,
                        HasAttachments INTEGER NOT NULL DEFAULT 0, IsSpam INTEGER NOT NULL DEFAULT 0, SpamScore REAL NOT NULL DEFAULT 0,
                        SpamReasons TEXT NOT NULL DEFAULT '', Category INTEGER NOT NULL DEFAULT 0, ListUnsubscribe TEXT NOT NULL DEFAULT '',
                        Size INTEGER NOT NULL DEFAULT 0,
                        UNIQUE(AccountId, Folder, FolderPath, Uid));
                    INSERT INTO Messages_v2(Id,AccountId,Folder,FolderPath,Uid,UidValidity,MessageId,InReplyTo,Refs,Subject,FromName,FromAddress,ReplyTo,
                        ToList,CcList,DateUnix,Preview,IsRead,IsFlagged,IsAnswered,HasAttachments,IsSpam,SpamScore,SpamReasons,Category,ListUnsubscribe,Size)
                    SELECT Id,AccountId,Folder,'',Uid,UidValidity,MessageId,InReplyTo,Refs,Subject,FromName,FromAddress,ReplyTo,
                        ToList,CcList,DateUnix,Preview,IsRead,IsFlagged,IsAnswered,HasAttachments,IsSpam,SpamScore,SpamReasons,Category,ListUnsubscribe,Size
                    FROM Messages;
                    DROP TABLE Messages;
                    ALTER TABLE Messages_v2 RENAME TO Messages;
                    CREATE INDEX IF NOT EXISTS IX_Messages_List ON Messages(AccountId, Folder, FolderPath, DateUnix DESC);
                    CREATE INDEX IF NOT EXISTS IX_Messages_Date ON Messages(Folder, DateUnix DESC);
                    CREATE INDEX IF NOT EXISTS IX_Messages_From ON Messages(FromAddress);
                    CREATE INDEX IF NOT EXISTS IX_Messages_MessageId ON Messages(AccountId, MessageId);
                    """, transaction: tx);
            }

            c.Execute("""
                CREATE TABLE IF NOT EXISTS Folders(AccountId INTEGER NOT NULL, Path TEXT NOT NULL, Name TEXT NOT NULL DEFAULT '',
                    Depth INTEGER NOT NULL DEFAULT 0, Unread INTEGER NOT NULL DEFAULT 0, Total INTEGER NOT NULL DEFAULT 0,
                    Selectable INTEGER NOT NULL DEFAULT 1, SortKey TEXT NOT NULL DEFAULT '', PRIMARY KEY(AccountId, Path));
                CREATE TABLE IF NOT EXISTS Tags(Id INTEGER PRIMARY KEY AUTOINCREMENT, TagGuid TEXT NOT NULL DEFAULT '', Name TEXT NOT NULL,
                    Color TEXT NOT NULL DEFAULT '#7C6CFF');
                CREATE TABLE IF NOT EXISTS MessageTags(AccountId INTEGER NOT NULL, MessageKey TEXT NOT NULL, TagId INTEGER NOT NULL,
                    PRIMARY KEY(AccountId, MessageKey, TagId));
                CREATE TABLE IF NOT EXISTS TagRules(TagId INTEGER NOT NULL, Pattern TEXT NOT NULL, CreatedUnix INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(TagId, Pattern));
                CREATE TABLE IF NOT EXISTS ServerTemplates(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL,
                    ImapHost TEXT NOT NULL, ImapPort INTEGER NOT NULL, ImapSecurity INTEGER NOT NULL,
                    SmtpHost TEXT NOT NULL, SmtpPort INTEGER NOT NULL, SmtpSecurity INTEGER NOT NULL);
                """, transaction: tx);
            c.Execute("PRAGMA user_version=2", transaction: tx);
            tx.Commit();
        }
    }

    private static bool HasColumn(SqliteConnection c, string table, string column) =>
        c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").Contains(column, StringComparer.OrdinalIgnoreCase);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Accounts(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DisplayName TEXT NOT NULL DEFAULT '', Email TEXT NOT NULL DEFAULT '', SenderName TEXT NOT NULL DEFAULT '',
            Color TEXT NOT NULL DEFAULT '#7C6CFF',
            ImapHost TEXT NOT NULL DEFAULT '', ImapPort INTEGER NOT NULL DEFAULT 993, ImapSecurity INTEGER NOT NULL DEFAULT 2,
            SmtpHost TEXT NOT NULL DEFAULT '', SmtpPort INTEGER NOT NULL DEFAULT 465, SmtpSecurity INTEGER NOT NULL DEFAULT 2,
            Username TEXT NOT NULL DEFAULT '', ProtectedPassword TEXT NOT NULL DEFAULT '',
            Signature TEXT NOT NULL DEFAULT '', SortOrder INTEGER NOT NULL DEFAULT 0, LastSyncUnix INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE IF NOT EXISTS Messages(
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            AccountId INTEGER NOT NULL, Folder INTEGER NOT NULL, Uid INTEGER NOT NULL, UidValidity INTEGER NOT NULL DEFAULT 0,
            MessageId TEXT NOT NULL DEFAULT '', InReplyTo TEXT NOT NULL DEFAULT '', Refs TEXT NOT NULL DEFAULT '',
            Subject TEXT NOT NULL DEFAULT '', FromName TEXT NOT NULL DEFAULT '', FromAddress TEXT NOT NULL DEFAULT '',
            ReplyTo TEXT NOT NULL DEFAULT '', ToList TEXT NOT NULL DEFAULT '', CcList TEXT NOT NULL DEFAULT '',
            DateUnix INTEGER NOT NULL DEFAULT 0, Preview TEXT NOT NULL DEFAULT '',
            IsRead INTEGER NOT NULL DEFAULT 0, IsFlagged INTEGER NOT NULL DEFAULT 0, IsAnswered INTEGER NOT NULL DEFAULT 0,
            HasAttachments INTEGER NOT NULL DEFAULT 0, IsSpam INTEGER NOT NULL DEFAULT 0, SpamScore REAL NOT NULL DEFAULT 0,
            SpamReasons TEXT NOT NULL DEFAULT '', Category INTEGER NOT NULL DEFAULT 0, ListUnsubscribe TEXT NOT NULL DEFAULT '',
            Size INTEGER NOT NULL DEFAULT 0,
            UNIQUE(AccountId, Folder, Uid));
        CREATE INDEX IF NOT EXISTS IX_Messages_List ON Messages(AccountId, Folder, DateUnix DESC);
        CREATE INDEX IF NOT EXISTS IX_Messages_Date ON Messages(Folder, DateUnix DESC);
        CREATE INDEX IF NOT EXISTS IX_Messages_From ON Messages(FromAddress);

        CREATE TABLE IF NOT EXISTS BlockedSenders(Pattern TEXT PRIMARY KEY, CreatedUnix INTEGER NOT NULL DEFAULT 0, Hits INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS TrustedSenders(Address TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS Contacts(Address TEXT PRIMARY KEY, Name TEXT NOT NULL DEFAULT '',
            SentCount INTEGER NOT NULL DEFAULT 0, ReceivedCount INTEGER NOT NULL DEFAULT 0, LastSeenUnix INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS SpamTokens(Token TEXT PRIMARY KEY, Spam INTEGER NOT NULL DEFAULT 0, Ham INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS Counters(Key TEXT PRIMARY KEY, Value INTEGER NOT NULL DEFAULT 0);
        """;
}
