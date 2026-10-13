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
    }

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
