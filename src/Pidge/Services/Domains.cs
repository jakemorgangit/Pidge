namespace Pidge.Services;

/// <summary>Helpers for working with sender domains.</summary>
public static class Domains
{
    /// <summary>Free/personal mailbox providers: mail from these is from a person, not an organisation.</summary>
    private static readonly HashSet<string> PersonalProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "outlook.co.uk", "hotmail.com", "hotmail.co.uk", "hotmail.fr", "live.com",
        "live.co.uk", "msn.com", "passport.com", "yahoo.com", "yahoo.co.uk", "yahoo.fr", "ymail.com", "rocketmail.com", "aol.com",
        "aol.co.uk", "aim.com", "icloud.com", "me.com", "mac.com", "proton.me", "protonmail.com", "protonmail.ch", "pm.me",
        "gmx.com", "gmx.co.uk", "gmx.net", "gmx.de", "web.de", "mail.com", "email.com", "zoho.com", "zohomail.com", "yandex.com",
        "yandex.ru", "mail.ru", "btinternet.com", "btopenworld.com", "sky.com", "virginmedia.com", "ntlworld.com", "blueyonder.co.uk",
        "talktalk.net", "tiscali.co.uk", "hey.com", "fastmail.com", "fastmail.fm", "tutanota.com", "tuta.io", "qq.com", "163.com",
        "126.com", "hushmail.com", "inbox.com", "duck.com", "mailbox.org", "posteo.de", "laposte.net", "orange.fr", "free.fr"
    };

    // Second-level suffixes where the registrable domain has three labels (example.co.uk).
    private static readonly HashSet<string> TwoPartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "ltd.uk", "plc.uk", "me.uk", "net.uk", "com.au", "net.au", "org.au", "co.nz", "org.nz",
        "co.za", "com.br", "com.mx", "co.jp", "co.in", "com.sg", "com.hk", "co.kr", "com.tr", "com.cn", "co.il", "com.ar"
    };

    /// <summary>The registrable domain: "news.mail.example.co.uk" → "example.co.uk".</summary>
    public static string BaseDomain(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return host;
        var lastTwo = parts[^2] + "." + parts[^1];
        return string.Join('.', parts.TakeLast(TwoPartSuffixes.Contains(lastTwo) ? 3 : 2));
    }

    public static string DomainOf(string address) =>
        address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..].Trim().Trim('>').ToLowerInvariant() : "";

    public static bool IsPersonalProvider(string domain) =>
        PersonalProviders.Contains(domain) || PersonalProviders.Contains(BaseDomain(domain));

    /// <summary>True when the sender looks like an organisation (its own domain) rather than a personal mailbox.</summary>
    public static bool IsOrganisation(string address)
    {
        var domain = DomainOf(address);
        return domain.Contains('.') && !IsPersonalProvider(domain);
    }
}
