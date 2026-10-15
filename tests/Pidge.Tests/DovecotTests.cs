using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using Pidge.Models;
using Pidge.Services;
using Xunit.Abstractions;
using MailStore = Pidge.Data.MailStore;

namespace Pidge.Tests;

/// <summary>
/// Runs against a real Dovecot server (what DreamHost and many hosts use), which is stricter than GreenMail:
/// it rejects any command line over 64 KB. Start one with:
///   docker run -d --rm --name pidge-dovecot -p 14143:143 dovecot/dovecot:2.3.21
///   docker exec pidge-dovecot sh -c "echo 'disable_plaintext_auth = no' >> /etc/dovecot/dovecot.conf &amp;&amp; doveadm reload"
/// These tests are skipped when it isn't running.
/// </summary>
[Collection("GreenMail")]
public class DovecotTests(GreenMailFixture server, ITestOutputHelper output)
{
    private const int Port = 14143;

    private static bool DovecotRunning()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            return tcp.ConnectAsync("127.0.0.1", Port).Wait(1000) && tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>Fills a mailbox with <paramref name="count"/> multipart messages, then deletes every other one so UIDs have gaps.</summary>
    private static async Task FillWithGapsAsync(string user, int count)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None);
        await imap.AuthenticateAsync(user, "pass");
        var requests = new List<IAppendRequest>();
        for (int i = 0; i < count; i++)
        {
            var msg = new MimeMessage();
            msg.From.Add(MailboxAddress.Parse($"sender{i % 50}@example.org"));
            msg.To.Add(MailboxAddress.Parse(user));
            msg.Subject = $"Message {i}";
            var body = new BodyBuilder { TextBody = $"Plain body {i}", HtmlBody = $"<p>HTML body {i}</p>" };
            msg.Body = body.ToMessageBody();
            requests.Add(new AppendRequest(msg, MessageFlags.Seen));
            if (requests.Count == 200 || i == count - 1)
            {
                await imap.Inbox.AppendAsync(requests);
                requests.Clear();
            }
        }
        await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
        var all = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.All);
        var everyOther = all.Where((_, idx) => idx % 2 == 1).ToList();
        foreach (var chunk in everyOther.Chunk(50)) // small commands, so this works under a tight line limit too
            await imap.Inbox.StoreAsync(chunk, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
        await imap.Inbox.ExpungeAsync();
        await imap.DisconnectAsync(true);
    }

    [Fact]
    public async Task Syncs_a_large_mailbox_with_gaps_without_overlong_commands()
    {
        if (!DovecotRunning()) { output.WriteLine("Dovecot not running: skipped"); return; }

        var user = $"big{Guid.NewGuid():N}"[..12] + "@test";
        await FillWithGapsAsync(user, 7000); // ~3,500 left, with a gap between every UID

        var account = new MailAccount
        {
            DisplayName = "Dovecot", Email = user, Username = user, ProtectedPassword = Secrets.Protect("pass"),
            ImapHost = "127.0.0.1", ImapPort = Port, ImapSecurity = (int)SecureSocketOptions.None,
            SmtpHost = "127.0.0.1", SmtpPort = 25, SmtpSecurity = 0
        };
        MailStore.InsertAccount(account);
        MailEngine.Instance.Register(account);

        string? error = null;
        void OnState(long id, bool syncing, string? err) { if (id == account.Id && err != null) error = err; }
        MailEngine.Instance.SyncState += OnState;
        var saved = AppSettings.Current.InitialSyncCount;
        try
        {
            AppSettings.Current.InitialSyncCount = 3000;
            await MailEngine.Instance.SyncFolderAsync(account.Id, FolderKind.Inbox);
            var cached = MailStore.QueryMessages(account.Id, FolderKind.Inbox, Pidge.Data.MessageFilter.All, null, 10_000).Count;
            output.WriteLine($"first sync: {cached} messages, error={error}");
            Assert.Null(error);
            Assert.Equal(3000, cached);

            // The second sync checks every cached message's flags: this is where long UID lists can appear.
            await MailEngine.Instance.SyncFolderAsync(account.Id, FolderKind.Inbox);
            output.WriteLine($"second sync: error={error}");
            Assert.Null(error);

            // And new mail keeps arriving afterwards.
            using (var imap = new ImapClient())
            {
                await imap.ConnectAsync("127.0.0.1", Port, SecureSocketOptions.None);
                await imap.AuthenticateAsync(user, "pass");
                var fresh = new MimeMessage();
                fresh.From.Add(MailboxAddress.Parse("new@example.org"));
                fresh.To.Add(MailboxAddress.Parse(user));
                fresh.Subject = "Brand new";
                fresh.Body = new TextPart("plain") { Text = "Hello" };
                await imap.Inbox.AppendAsync(fresh, MessageFlags.None);
                await imap.DisconnectAsync(true);
            }
            await MailEngine.Instance.SyncFolderAsync(account.Id, FolderKind.Inbox);
            Assert.Null(error);
            Assert.Contains(MailStore.QueryMessages(account.Id, FolderKind.Inbox, Pidge.Data.MessageFilter.All, null, 10), m => m.Subject == "Brand new");
        }
        finally
        {
            AppSettings.Current.InitialSyncCount = saved;
            MailEngine.Instance.SyncState -= OnState;
        }
    }
}
