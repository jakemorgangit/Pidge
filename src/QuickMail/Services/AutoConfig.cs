using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Xml.Linq;

namespace QuickMail.Services;

public enum ProviderKind { Other, Google, Microsoft, Yahoo, ICloud }

public record ServerConfig(string ImapHost, int ImapPort, int ImapSecurity, string SmtpHost, int SmtpPort, int SmtpSecurity,
    string Provider, string? Note = null, ProviderKind Kind = ProviderKind.Other);

/// <summary>
/// Works out IMAP/SMTP settings from an email address: known providers, then the domain's MX records,
/// then the Thunderbird ISPDB / domain autoconfig, then a probe of common host names.
/// </summary>
public static class AutoConfig
{
    // MailKit SecureSocketOptions: 2 = SslOnConnect, 3 = StartTls
    private const int Ssl = 2, StartTls = 3;

    private const string AppPasswordNote = "This provider needs an app password rather than your normal password if two-step sign-in is on.";

    private static readonly ServerConfig Gmail = new("imap.gmail.com", 993, Ssl, "smtp.gmail.com", 465, Ssl, "Google",
        "Google needs an app password. QuickMail will walk you through it.", ProviderKind.Google);
    private static readonly ServerConfig Microsoft = new("outlook.office365.com", 993, Ssl, "smtp.office365.com", 587, StartTls, "Microsoft",
        "Microsoft accounts sign in through Microsoft's own sign-in page.", ProviderKind.Microsoft);
    private static readonly ServerConfig Yahoo = new("imap.mail.yahoo.com", 993, Ssl, "smtp.mail.yahoo.com", 465, Ssl, "Yahoo", AppPasswordNote, ProviderKind.Yahoo);
    private static readonly ServerConfig ICloud = new("imap.mail.me.com", 993, Ssl, "smtp.mail.me.com", 587, StartTls, "iCloud",
        "iCloud needs an app-specific password. QuickMail will walk you through it.", ProviderKind.ICloud);

    private static readonly Dictionary<string, ServerConfig> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = Gmail, ["googlemail.com"] = Gmail,
        ["outlook.com"] = Microsoft, ["hotmail.com"] = Microsoft, ["hotmail.co.uk"] = Microsoft, ["live.com"] = Microsoft,
        ["live.co.uk"] = Microsoft, ["msn.com"] = Microsoft, ["outlook.co.uk"] = Microsoft,
        ["yahoo.com"] = Yahoo, ["yahoo.co.uk"] = Yahoo, ["ymail.com"] = Yahoo, ["rocketmail.com"] = Yahoo,
        ["icloud.com"] = ICloud, ["me.com"] = ICloud, ["mac.com"] = ICloud,
        ["aol.com"] = new("imap.aol.com", 993, Ssl, "smtp.aol.com", 465, Ssl, "AOL", AppPasswordNote),
        ["zoho.com"] = new("imap.zoho.com", 993, Ssl, "smtp.zoho.com", 465, Ssl, "Zoho"),
        ["zohomail.com"] = new("imap.zoho.com", 993, Ssl, "smtp.zoho.com", 465, Ssl, "Zoho"),
        ["fastmail.com"] = new("imap.fastmail.com", 993, Ssl, "smtp.fastmail.com", 465, Ssl, "Fastmail", AppPasswordNote),
        ["gmx.com"] = new("imap.gmx.com", 993, Ssl, "mail.gmx.com", 587, StartTls, "GMX"),
        ["gmx.co.uk"] = new("imap.gmx.co.uk", 993, Ssl, "mail.gmx.co.uk", 587, StartTls, "GMX"),
        ["yandex.com"] = new("imap.yandex.com", 993, Ssl, "smtp.yandex.com", 465, Ssl, "Yandex", AppPasswordNote),
        ["proton.me"] = new("127.0.0.1", 1143, StartTls, "127.0.0.1", 1025, StartTls, "Proton", "Proton Mail works through Proton Mail Bridge running on this PC."),
        ["protonmail.com"] = new("127.0.0.1", 1143, StartTls, "127.0.0.1", 1025, StartTls, "Proton", "Proton Mail works through Proton Mail Bridge running on this PC."),
    };

    private static readonly ServerConfig DreamHost = new("imap.dreamhost.com", 993, Ssl, "smtp.dreamhost.com", 465, Ssl, "DreamHost");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<ServerConfig?> DiscoverAsync(string email, CancellationToken ct = default)
    {
        var at = email.LastIndexOf('@');
        if (at < 1) return null;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();

        if (Known.TryGetValue(domain, out var known)) return known;

        // Custom domains hosted by big providers: check MX records.
        var mx = await LookupMxAsync(domain, ct);
        if (mx.Any(h => h.EndsWith("google.com") || h.EndsWith("googlemail.com"))) return Gmail with { Provider = "Google Workspace" };
        if (mx.Any(h => h.EndsWith("outlook.com") || h.EndsWith("office365.com"))) return Microsoft with { Provider = "Microsoft 365" };
        if (mx.Any(h => h.EndsWith("zoho.com") || h.EndsWith("zoho.eu"))) return Known["zoho.com"];
        if (mx.Any(h => h.EndsWith("messagingengine.com"))) return Known["fastmail.com"];
        if (mx.Any(h => h.EndsWith("icloud.com"))) return ICloud;
        if (mx.Any(h => h.EndsWith("dreamhost.com"))) return DreamHost;

        var fromIspdb = await TryIspdbAsync($"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}", ct)
                        ?? await TryIspdbAsync($"https://autoconfig.thunderbird.net/v1.1/{domain}", ct);
        if (fromIspdb != null) return fromIspdb;

        foreach (var mxHost in mx)
        {
            var baseDomain = string.Join('.', mxHost.Split('.').TakeLast(2));
            var viaMx = await TryIspdbAsync($"https://autoconfig.thunderbird.net/v1.1/{baseDomain}", ct);
            if (viaMx != null) return viaMx;
        }

        return await ProbeAsync(domain, mx, ct);
    }

    private static async Task<List<string>> LookupMxAsync(string domain, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(domain)}&type=MX");
            req.Headers.Add("Accept", "application/dns-json");
            using var resp = await Http.SendAsync(req, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("Answer", out var answers)) return [];
            return answers.EnumerateArray()
                .Select(a => a.GetProperty("data").GetString() ?? "")
                .Select(d => d.Split(' ').Last().TrimEnd('.').ToLowerInvariant())
                .Where(h => h.Length > 0)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<ServerConfig?> TryIspdbAsync(string url, CancellationToken ct)
    {
        try
        {
            var xml = await Http.GetStringAsync(url, ct);
            var doc = XDocument.Parse(xml);
            var provider = doc.Descendants("emailProvider").FirstOrDefault();
            var imap = doc.Descendants("incomingServer").FirstOrDefault(e => (string?)e.Attribute("type") == "imap");
            var smtp = doc.Descendants("outgoingServer").FirstOrDefault(e => (string?)e.Attribute("type") == "smtp");
            if (imap == null || smtp == null) return null;
            return new ServerConfig(
                (string?)imap.Element("hostname") ?? "", (int?)imap.Element("port") ?? 993, Security((string?)imap.Element("socketType")),
                (string?)smtp.Element("hostname") ?? "", (int?)smtp.Element("port") ?? 465, Security((string?)smtp.Element("socketType")),
                (string?)provider?.Element("displayShortName") ?? "Auto-detected");
        }
        catch
        {
            return null;
        }

        static int Security(string? socketType) => socketType?.ToUpperInvariant() switch
        {
            "SSL" => Ssl,
            "STARTTLS" => StartTls,
            "PLAIN" => 0,
            _ => 1
        };
    }

    private static async Task<ServerConfig?> ProbeAsync(string domain, List<string> mx, CancellationToken ct)
    {
        var imapCandidates = new[] { $"imap.{domain}", $"mail.{domain}", domain }.Concat(mx).Distinct();
        string? imapHost = null;
        foreach (var h in imapCandidates)
            if (await CanConnectAsync(h, 993, ct)) { imapHost = h; break; }
        if (imapHost == null) return null;

        var smtpCandidates = new[] { $"smtp.{domain}", $"mail.{domain}", imapHost }.Distinct();
        foreach (var h in smtpCandidates)
        {
            if (await CanConnectAsync(h, 465, ct)) return new(imapHost, 993, Ssl, h, 465, Ssl, "Detected");
            if (await CanConnectAsync(h, 587, ct)) return new(imapHost, 993, Ssl, h, 587, StartTls, "Detected");
        }
        return new(imapHost, 993, Ssl, imapHost, 465, Ssl, "Best guess");
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
