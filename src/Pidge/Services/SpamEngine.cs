using System.Text.RegularExpressions;
using MimeKit;
using Pidge.Data;
using Pidge.Models;

namespace Pidge.Services;

/// <summary>
/// Spam and pattern detection. Combines rule-based heuristics (server verdicts, SPF/DKIM/DMARC, impersonation,
/// suspicious wording) with a naive Bayes classifier that learns from every "spam" / "not spam" decision you make.
/// </summary>
public static partial class SpamEngine
{
    private static readonly object Gate = new();
    private static Dictionary<string, (int spam, int ham)> _tokens = new();
    private static readonly Dictionary<string, (int spam, int ham)> _dirty = new();
    private static List<string> _blocked = [];
    private static HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    public static long SpamDocs => MailStore.GetCounter("spamDocs");
    public static long HamDocs => MailStore.GetCounter("hamDocs");

    private static readonly string[] SpamPhrases =
    [
        "viagra", "cialis", "casino", "lottery", "you have won", "winner", "jackpot", "bitcoin", "crypto", "investment opportunity",
        "act now", "limited time", "risk-free", "risk free", "guaranteed", "congratulations", "claim your", "free gift", "100% free",
        "click here", "verify your account", "account suspended", "account has been suspended", "password expired", "unusual activity",
        "unusual sign-in", "wire transfer", "inheritance", "beneficiary", "loan approved", "pre-approved", "weight loss", "miracle",
        "pharmacy", "hot singles", "million dollars", "earn money", "work from home", "make money", "final notice",
        "you have been selected", "gift card", "urgent response", "confirm your identity", "payment failed", "invoice attached",
        "your mailbox is full", "storage is full", "unclaimed", "refund pending", "tax refund", "parcel could not be delivered"
    ];

    private static readonly string[] Brands =
    [
        "paypal", "apple", "microsoft", "amazon", "netflix", "hmrc", "irs", "dhl", "fedex", "royal mail", "docusign", "office 365",
        "outlook", "google", "facebook", "instagram", "coinbase", "binance", "hsbc", "barclays", "lloyds", "natwest", "santander",
        "chase", "wells fargo", "ebay", "linkedin", "dropbox", "adobe", "evri", "dpd", "norton", "mcafee", "geek squad"
    ];

    private static readonly string[] RiskyTlds =
        [".xyz", ".top", ".click", ".loan", ".work", ".gq", ".tk", ".ml", ".cf", ".ga", ".icu", ".buzz", ".monster", ".rest", ".cam", ".cyou", ".sbs", ".zip", ".mov"];

    private static readonly string[] SocialDomains =
        ["facebook", "facebookmail", "linkedin", "twitter", "x.com", "instagram", "tiktok", "reddit", "pinterest", "youtube", "discord", "nextdoor", "meetup", "strava"];

    private static readonly string[] NotificationPrefixes =
        ["noreply", "no-reply", "donotreply", "do-not-reply", "notifications", "notification", "notify", "alerts", "alert", "mailer-daemon", "postmaster", "updates", "security", "account", "info", "support", "billing", "receipts", "orders"];

    private static readonly string[] PromoWords =
        ["% off", "sale", "discount", "deal", "offer", "voucher", "coupon", "promo", "black friday", "cyber monday", "free shipping", "free delivery", "save £", "save $", "last chance", "ends tonight", "new arrivals", "shop now"];

    public static void Load()
    {
        lock (Gate)
        {
            _tokens = MailStore.LoadTokens();
            _blocked = MailStore.GetBlocked().Select(b => b.Pattern).ToList();
            _known = MailStore.KnownCorrespondents();
        }
    }

    public static void RefreshLists()
    {
        lock (Gate)
        {
            _blocked = MailStore.GetBlocked().Select(b => b.Pattern).ToList();
            _known = MailStore.KnownCorrespondents();
        }
    }

    // ---------------- Blocking ----------------
    /// <summary>Returns the matching block pattern ("someone@x.com" or "@x.com"), or null.</summary>
    public static string? MatchBlocked(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        var a = address.ToLowerInvariant();
        lock (Gate)
        {
            foreach (var p in _blocked)
            {
                if (p.StartsWith('@') ? a.EndsWith(p) || a.EndsWith("." + p[1..]) : a == p) return p;
            }
        }
        return null;
    }

    public static bool IsKnownCorrespondent(string address)
    {
        lock (Gate) return _known.Contains(address);
    }

    // ---------------- Categorisation ----------------
    public static MailCategory Categorize(MessageRecord m, HeaderList? headers)
    {
        var from = m.FromAddress.ToLowerInvariant();
        var domain = m.FromDomain;
        var local = from.Contains('@') ? from[..from.IndexOf('@')] : from;
        var text = (m.Subject + " " + m.Preview).ToLowerInvariant();

        if (SocialDomains.Any(s => domain.Contains(s))) return MailCategory.Social;

        bool bulk = !string.IsNullOrEmpty(m.ListUnsubscribe)
                    || headers?.Contains("List-Id") == true
                    || (headers?["Precedence"]?.Contains("bulk", StringComparison.OrdinalIgnoreCase) ?? false);

        if (bulk && PromoWords.Any(text.Contains)) return MailCategory.Promotion;
        if (bulk) return MailCategory.Newsletter;
        if (NotificationPrefixes.Any(p => local == p || local.StartsWith(p + ".") || local.StartsWith(p + "-") || local.StartsWith(p + "_") || local.StartsWith(p + "+")))
            return MailCategory.Notification;
        if (PromoWords.Count(text.Contains) >= 2) return MailCategory.Promotion;
        return MailCategory.Personal;
    }

    // ---------------- Scoring ----------------
    public static (double score, string reasons) Score(MessageRecord m, HeaderList? headers, string accountEmail)
    {
        if (IsKnownCorrespondent(m.FromAddress)) return (0, "");

        var reasons = new List<string>();
        double pts = 0;
        void Add(double p, string why) { pts += p; reasons.Add(why); }

        if (headers != null)
        {
            if ((headers["X-Spam-Flag"] ?? "").Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase)) Add(6, "Your mail server flagged it as spam");
            else if ((headers["X-Spam-Status"] ?? "").Trim().StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) Add(5, "Your mail server flagged it as spam");

            var auth = (headers["Authentication-Results"] ?? "").ToLowerInvariant();
            if (auth.Contains("dmarc=fail")) Add(3, "Failed DMARC sender check");
            if (auth.Contains("spf=fail")) Add(2, "Failed SPF sender check");
            else if (auth.Contains("spf=softfail")) Add(1, "SPF soft-fail");
            if (auth.Contains("dkim=fail")) Add(1.5, "Failed DKIM signature check");
        }

        var subject = m.Subject ?? "";
        var letters = subject.Where(char.IsLetter).ToArray();
        if (letters.Length >= 8 && letters.Count(char.IsUpper) > letters.Length * 0.7) Add(1.5, "Subject is shouting in capitals");
        if (subject.Count(ch => ch == '!') >= 3 || subject.Contains("$$") || subject.Contains("££")) Add(1, "Excessive punctuation in subject");
        if (string.IsNullOrWhiteSpace(subject)) Add(0.5, "No subject");

        var text = (subject + " " + m.Preview).ToLowerInvariant();
        var phraseHits = SpamPhrases.Where(text.Contains).Take(4).ToList();
        if (phraseHits.Count > 0) Add(1.2 * phraseHits.Count, $"Spam-like wording (\"{string.Join("\", \"", phraseHits)}\")");

        var name = (m.FromName ?? "").ToLowerInvariant();
        var domain = m.FromDomain;
        var domainAlnum = new string(domain.Where(char.IsLetterOrDigit).ToArray());
        foreach (var brand in Brands)
        {
            var b = brand.Replace(" ", "");
            if (name.Contains(brand) && !domainAlnum.Contains(b))
            {
                Add(3, $"Claims to be {brand} but sent from {domain}");
                break;
            }
        }

        var nameEmail = EmailInText().Match(name);
        if (nameEmail.Success && !string.Equals(nameEmail.Value, m.FromAddress, StringComparison.OrdinalIgnoreCase))
            Add(2.5, "Display name shows a different email address");

        var localPart = m.FromAddress.Contains('@') ? m.FromAddress[..m.FromAddress.IndexOf('@')] : "";
        if (localPart.Count(char.IsDigit) >= 5 || domain.Count(char.IsDigit) >= 4) Add(1, "Random-looking sender address");
        if (RiskyTlds.Any(domain.EndsWith)) Add(1.5, "Sender uses a high-risk domain extension");

        if (!string.IsNullOrEmpty(m.ReplyTo) && m.ReplyTo.Contains('@'))
        {
            var rtDomain = m.ReplyTo[(m.ReplyTo.LastIndexOf('@') + 1)..].Trim('>', ' ').ToLowerInvariant();
            if (domain.Length > 0 && !rtDomain.EndsWith(BaseDomain(domain)) && string.IsNullOrEmpty(m.ListUnsubscribe))
                Add(1.5, "Replies go to a different domain");
        }

        if (!string.IsNullOrEmpty(accountEmail) && m.ToList.Length > 0
            && !m.ToList.Contains(accountEmail, StringComparison.OrdinalIgnoreCase)
            && !m.CcList.Contains(accountEmail, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(m.ListUnsubscribe))
            Add(0.5, "You were not a visible recipient");

        double heuristic = 1 - Math.Exp(-pts / 4.0);
        double score = heuristic;

        var bayes = Classify(Tokenize(m));
        if (bayes.HasValue)
        {
            score = 0.5 * heuristic + 0.5 * bayes.Value;
            if (bayes.Value > 0.9) reasons.Insert(0, "Similar to messages you marked as spam");
            if (bayes.Value > 0.97 && score < bayes.Value - 0.1) score = Math.Max(score, 0.8);
        }

        return (Math.Round(score, 3), string.Join("\n", reasons));
    }

    /// <summary>Checks rendered HTML for links whose visible text points at a different site than the real target.</summary>
    public static string? FindDeceptiveLinks(string html)
    {
        foreach (Match match in AnchorRegex().Matches(html))
        {
            var href = match.Groups["href"].Value;
            var label = StripTags().Replace(match.Groups["text"].Value, "").Trim();
            if (!Uri.TryCreate(href, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) continue;
            var shown = UrlInText().Match(label);
            if (!shown.Success) continue;
            var shownHost = shown.Groups["host"].Value.ToLowerInvariant();
            var realHost = target.Host.ToLowerInvariant();
            if (!realHost.EndsWith(BaseDomain(shownHost)) && !shownHost.EndsWith(BaseDomain(realHost)))
                return $"A link shows \"{shownHost}\" but actually goes to \"{realHost}\".";
        }
        return null;
    }

    private static string BaseDomain(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        // Treat co.uk / com.au style suffixes as part of the base domain.
        var tail = parts[^2].Length <= 3 && parts[^1].Length == 2 ? 3 : 2;
        return string.Join('.', parts.TakeLast(Math.Min(tail, parts.Length)));
    }

    // ---------------- Naive Bayes ----------------
    public static HashSet<string> Tokenize(MessageRecord m)
    {
        var set = new HashSet<string>();
        foreach (Match w in WordRegex().Matches((m.Subject + " " + m.Preview).ToLowerInvariant()))
            if (w.Value.Length is >= 3 and <= 24) set.Add(w.Value);
        foreach (Match w in WordRegex().Matches((m.FromName ?? "").ToLowerInvariant()))
            set.Add("name:" + w.Value);
        if (m.FromAddress.Length > 0) set.Add("from:" + m.FromAddress.ToLowerInvariant());
        if (m.FromDomain.Length > 0) set.Add("dom:" + m.FromDomain);
        if (m.HasAttachments) set.Add("meta:attach");
        if (!string.IsNullOrEmpty(m.ListUnsubscribe)) set.Add("meta:list");
        return set;
    }

    private static double? Classify(HashSet<string> tokens)
    {
        long nSpam = SpamDocs, nHam = HamDocs;
        if (nSpam < 5 || nHam < 5) return null;

        var probs = new List<double>();
        lock (Gate)
        {
            foreach (var t in tokens)
            {
                if (!_tokens.TryGetValue(t, out var c) || c.spam + c.ham < 2) continue;
                double ps = Math.Min(1, (double)c.spam / nSpam);
                double ph = Math.Min(1, (double)c.ham / nHam);
                double p = ps / (ps + ph);
                // Weight towards 0.5 for rarely seen tokens (Robinson's adjustment).
                double n = c.spam + c.ham;
                p = (0.5 + n * p) / (1 + n);
                probs.Add(Math.Clamp(p, 0.01, 0.99));
            }
        }
        if (probs.Count == 0) return null;

        var interesting = probs.OrderByDescending(p => Math.Abs(p - 0.5)).Take(15);
        double logSpam = 0, logHam = 0;
        foreach (var p in interesting)
        {
            logSpam += Math.Log(p);
            logHam += Math.Log(1 - p);
        }
        return 1 / (1 + Math.Exp(logHam - logSpam));
    }

    public static void Train(MessageRecord m, bool isSpam)
    {
        var tokens = Tokenize(m);
        lock (Gate)
        {
            foreach (var t in tokens)
            {
                _tokens.TryGetValue(t, out var c);
                c = isSpam ? (c.spam + 1, c.ham) : (c.spam, c.ham + 1);
                _tokens[t] = c;
                _dirty[t] = c;
            }
        }
        MailStore.AddCounter(isSpam ? "spamDocs" : "hamDocs", 1);
        Flush();
    }

    public static void Flush()
    {
        List<KeyValuePair<string, (int, int)>> batch;
        lock (Gate)
        {
            batch = _dirty.ToList();
            _dirty.Clear();
        }
        if (batch.Count > 0) MailStore.SaveTokens(batch);
    }

    public static void Reset()
    {
        MailStore.ResetTokens();
        lock (Gate)
        {
            _tokens.Clear();
            _dirty.Clear();
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\-]*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailInText();

    [GeneratedRegex(@"<a\s[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTags();

    [GeneratedRegex(@"(?:https?://)?(?<host>(?:[a-z0-9\-]+\.)+[a-z]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();
}
