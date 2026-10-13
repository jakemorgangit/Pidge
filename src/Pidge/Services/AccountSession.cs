using System.IO;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Pidge.Models;

namespace Pidge.Services;

/// <summary>
/// One long-lived IMAP connection per account. MailKit clients are not thread-safe,
/// so every operation is serialised through <see cref="RunAsync{T}"/>.
/// </summary>
public sealed class AccountSession : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<FolderKind, IMailFolder?> _folders = new();
    private readonly Dictionary<string, IMailFolder> _customFolders = new(StringComparer.Ordinal);
    private ImapClient? _imap;

    public MailAccount Account { get; private set; }

    public AccountSession(MailAccount account) => Account = account;

    public void UpdateAccount(MailAccount account)
    {
        Account = account;
        _lock.Wait();
        try { Drop(); } finally { _lock.Release(); }
    }

    private bool ServerSavesSent =>
        Account.SmtpHost.Contains("gmail", StringComparison.OrdinalIgnoreCase)
        || Account.SmtpHost.Contains("office365", StringComparison.OrdinalIgnoreCase)
        || Account.SmtpHost.Contains("outlook", StringComparison.OrdinalIgnoreCase);

    private bool IsGmail => Account.ImapHost.Contains("gmail", StringComparison.OrdinalIgnoreCase);

    private async Task<ImapClient> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_imap is { IsConnected: true, IsAuthenticated: true }) return _imap;
        Drop();
        var client = new ImapClient { Timeout = 30_000 };
        await client.ConnectAsync(Account.ImapHost, Account.ImapPort, (SecureSocketOptions)Account.ImapSecurity, ct);
        await AuthenticateAsync(client, Account, null, ct);
        _imap = client;
        return client;
    }

    /// <summary>Password or OAuth sign-in. <paramref name="secret"/> overrides the stored password/token (used when testing).</summary>
    private static async Task AuthenticateAsync(MailService client, MailAccount account, string? secret, CancellationToken ct)
    {
        if (account.AuthMethod == AuthMethod.MicrosoftOAuth)
        {
            var token = secret ?? await MicrosoftAuth.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
        }
        else
        {
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            client.AuthenticationMechanisms.Remove("OAUTHBEARER");
            await client.AuthenticateAsync(account.Username, secret ?? Secrets.Unprotect(account.ProtectedPassword), ct);
        }
    }

    private void Drop()
    {
        _folders.Clear();
        _customFolders.Clear();
        if (_imap == null) return;
        try { if (_imap.IsConnected) _imap.Disconnect(true); } catch { /* ignore */ }
        _imap.Dispose();
        _imap = null;
    }

    public async Task<T> RunAsync<T>(Func<ImapClient, Task<T>> work, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var client = await EnsureConnectedAsync(ct);
                    return await work(client);
                }
                catch (Exception ex) when (attempt == 0 && IsConnectionProblem(ex))
                {
                    Drop(); // reconnect once and retry
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RunAsync(Func<ImapClient, Task> work, CancellationToken ct = default) =>
        RunAsync<bool>(async c => { await work(c); return true; }, ct);

    private static bool IsConnectionProblem(Exception ex) =>
        ex is IOException or SocketException or ServiceNotConnectedException or ServiceNotAuthenticatedException
            or ImapProtocolException or ObjectDisposedException;

    /// <summary>Finds the server folder for a logical folder kind (SPECIAL-USE first, then common names).</summary>
    public async Task<IMailFolder?> GetFolderAsync(ImapClient client, FolderKind kind, string path = "", CancellationToken ct = default)
    {
        if (kind == FolderKind.Inbox) return client.Inbox;
        if (kind == FolderKind.Custom)
        {
            if (_customFolders.TryGetValue(path, out var known)) return known;
            try
            {
                var f = await client.GetFolderAsync(path, ct);
                _customFolders[path] = f;
                return f;
            }
            catch (FolderNotFoundException)
            {
                return null;
            }
        }
        if (_folders.TryGetValue(kind, out var cached)) return cached;

        IMailFolder? found = null;
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            var special = kind switch
            {
                FolderKind.Sent => SpecialFolder.Sent,
                FolderKind.Drafts => SpecialFolder.Drafts,
                FolderKind.Junk => SpecialFolder.Junk,
                FolderKind.Trash => SpecialFolder.Trash,
                _ => IsGmail ? SpecialFolder.All : SpecialFolder.Archive
            };
            try { found = client.GetFolder(special); } catch { /* not supported */ }
        }

        if (found == null && client.PersonalNamespaces.Count > 0)
        {
            var all = await client.GetFoldersAsync(client.PersonalNamespaces[0], false, ct);
            string[] names = kind switch
            {
                FolderKind.Sent => ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "Sent-Mail", "Gesendet", "Envoyés"],
                FolderKind.Drafts => ["Drafts", "Draft", "Entwürfe", "Brouillons"],
                FolderKind.Junk => ["Junk", "Spam", "Junk E-mail", "Junk Email", "Bulk Mail", "Junk Mail"],
                FolderKind.Trash => ["Trash", "Deleted Items", "Deleted Messages", "Deleted", "Bin", "Papierkorb"],
                _ => ["Archive", "Archives", "All Mail", "Archived"]
            };
            found = all.FirstOrDefault(f => names.Any(n => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)));

            if (found == null && kind == FolderKind.Archive)
            {
                try
                {
                    var root = client.GetFolder(client.PersonalNamespaces[0]);
                    found = await root.CreateAsync("Archive", true, ct);
                }
                catch { /* server may not allow creating folders */ }
            }
        }

        _folders[kind] = found;
        return found;
    }

    public async Task SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        using (var smtp = new SmtpClient { Timeout = 60_000 })
        {
            await smtp.ConnectAsync(Account.SmtpHost, Account.SmtpPort, (SecureSocketOptions)Account.SmtpSecurity, ct);
            await AuthenticateAsync(smtp, Account, null, ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);
        }

        if (ServerSavesSent) return;
        try
        {
            await RunAsync(async c =>
            {
                var sent = await GetFolderAsync(c, FolderKind.Sent, "", ct);
                if (sent != null) await sent.AppendAsync(message, MessageFlags.Seen, ct);
            }, ct);
        }
        catch { /* the message was sent; failing to file a copy is not fatal */ }
    }

    /// <summary>
    /// Verifies IMAP and SMTP sign-in without touching the saved session.
    /// <paramref name="password"/> is the password, or an access token for OAuth accounts.
    /// </summary>
    public static async Task TestAsync(MailAccount account, string password, CancellationToken ct = default)
    {
        using (var imap = new ImapClient { Timeout = 20_000 })
        {
            try
            {
                await imap.ConnectAsync(account.ImapHost, account.ImapPort, (SecureSocketOptions)account.ImapSecurity, ct);
                await AuthenticateAsync(imap, account, password, ct);
                await imap.DisconnectAsync(true, ct);
            }
            catch (Exception ex) { throw new InvalidOperationException($"Incoming mail (IMAP): {Friendly(ex)}", ex); }
        }

        using var smtp = new SmtpClient { Timeout = 20_000 };
        try
        {
            await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, (SecureSocketOptions)account.SmtpSecurity, ct);
            await AuthenticateAsync(smtp, account, password, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex) { throw new InvalidOperationException($"Outgoing mail (SMTP): {Friendly(ex)}", ex); }
    }

    public static string Friendly(Exception ex) => ex switch
    {
        AuthenticationException => "the server rejected the username or password. If the provider uses two-step sign-in, create an app password.",
        SocketException se => $"couldn't reach the server ({se.SocketErrorCode}).",
        SslHandshakeException => "the secure connection failed. Try a different security option or port.",
        OperationCanceledException => "timed out.",
        _ => ex.Message
    };

    public void Dispose()
    {
        Drop();
        _lock.Dispose();
    }
}
