using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Pidge.Data;
using Pidge.Models;
using Pidge.Services;
using MailStore = Pidge.Data.MailStore;

namespace Pidge.Tests;

/// <summary>
/// Runs a throwaway GreenMail IMAP/SMTP server in Docker and points Pidge's data folder at a temp directory.
/// GreenMail accepts any password and creates a mailbox on first use.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    public const int ImapPort = 13143;
    public const int SmtpPort = 13025;
    private const string Container = "pidge-greenmail-tests";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "qm-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("PIDGE_DATA", DataDir);
        Db.Initialize();
        SpamEngine.Load();

        Docker($"rm -f {Container}");
        Docker($"run -d --rm --name {Container} -p {ImapPort}:3143 -p {SmtpPort}:3025 greenmail/standalone:2.1.2", throwOnError: true);

        // Wait until both ports accept connections and IMAP greets us.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var imap = new ImapClient();
                await imap.ConnectAsync("127.0.0.1", ImapPort, SecureSocketOptions.None);
                await imap.DisconnectAsync(true);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", SmtpPort);
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        throw new TimeoutException("GreenMail did not start.");
    }

    public Task DisposeAsync()
    {
        Docker($"rm -f {Container}");
        try { Directory.Delete(DataDir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static void Docker(string args, bool throwOnError = false)
    {
        using var p = Process.Start(new ProcessStartInfo("docker", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(120_000);
        if (throwOnError && p.ExitCode != 0) throw new InvalidOperationException("docker " + args + ": " + p.StandardError.ReadToEnd());
    }

    // ---------------- helpers used by tests ----------------
    public static string NewAddress(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + "@pidge.test";

    public MailAccount CreateAccount(string email)
    {
        var account = new MailAccount
        {
            DisplayName = "Test", Email = email, Username = email, ProtectedPassword = Secrets.Protect("secret"),
            ImapHost = "127.0.0.1", ImapPort = ImapPort, ImapSecurity = (int)SecureSocketOptions.None,
            SmtpHost = "127.0.0.1", SmtpPort = SmtpPort, SmtpSecurity = (int)SecureSocketOptions.None
        };
        MailStore.InsertAccount(account);
        MailEngine.Instance.Register(account);
        return account;
    }

    public static async Task SendAsync(string to, string subject, string from = "alice@example.org", string body = "Hello there")
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(from));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync("127.0.0.1", SmtpPort, SecureSocketOptions.None);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }

    /// <summary>Opens a second, independent IMAP connection (like another mail client would).</summary>
    public static async Task WithImapAsync(string user, Func<ImapClient, Task> work)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync("127.0.0.1", ImapPort, SecureSocketOptions.None);
        await imap.AuthenticateAsync(user, "secret");
        await work(imap);
        await imap.DisconnectAsync(true);
    }

    public static List<MessageRecord> Local(long accountId, FolderKind folder = FolderKind.Inbox) =>
        MailStore.QueryMessages(accountId, folder, MessageFilter.All, null, 1000)
            .Concat(MailStore.QueryMessages(accountId, folder, MessageFilter.Spam, null, 1000))
            .DistinctBy(m => m.Id)
            .ToList();
}

[CollectionDefinition("GreenMail")]
public class GreenMailCollection : ICollectionFixture<GreenMailFixture>;
