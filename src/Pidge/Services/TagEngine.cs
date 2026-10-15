using Pidge.Data;
using Pidge.Models;

namespace Pidge.Services;

/// <summary>
/// Your own tags, which learn. Tagging a message creates a rule for its sender, so their past and future mail is
/// tagged automatically. Once two or more different people at the same organisation carry a tag, the rule widens
/// to the whole domain (never for public mail providers like gmail.com).
/// </summary>
public static class TagEngine
{

    public static readonly string[] Palette =
        ["#7C6CFF", "#4CC9F0", "#36D399", "#FFB648", "#FF5D73", "#F472B6", "#A3E635", "#60A5FA", "#FB923C", "#2DD4BF"];

    public static bool Matches(string pattern, string address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        var a = address.ToLowerInvariant();
        return pattern.StartsWith('@') ? a.EndsWith(pattern) || a.EndsWith("." + pattern[1..]) : a == pattern;
    }

    /// <summary>Applies every learned rule to newly arrived messages.</summary>
    public static void ApplyRules(IEnumerable<MessageRecord> records)
    {
        var rules = MailStore.GetTagRules();
        if (rules.Count == 0) return;
        foreach (var r in records)
            foreach (var rule in rules)
                if (Matches(rule.Pattern, r.FromAddress))
                    MailStore.AddMessageTag(r.AccountId, r.MessageKey, rule.TagId);
    }

    /// <summary>
    /// Tags messages, learns from them and back-fills matching mail. Returns how many other messages were tagged automatically.
    /// </summary>
    public static int Tag(IEnumerable<MessageRecord> messages, TagInfo tag, bool learn = true)
    {
        var list = messages.ToList();
        foreach (var m in list) MailStore.AddMessageTag(m.AccountId, m.MessageKey, tag.Id);
        if (!learn) return 0;

        var patterns = new HashSet<string>(list.Select(m => m.FromAddress.ToLowerInvariant()).Where(a => a.Contains('@')));

        // Widen to the domain once several different people from the same organisation carry the tag.
        foreach (var group in MailStore.TaggedSenders(tag.Id).Concat(patterns).Distinct()
                     .GroupBy(a => a[(a.IndexOf('@') + 1)..]))
        {
            if (group.Count() >= 2 && !Domains.IsPersonalProvider(group.Key)) patterns.Add("@" + group.Key);
        }

        int auto = 0;
        var tagged = list.Select(m => (m.AccountId, m.MessageKey)).ToHashSet();
        foreach (var p in patterns)
        {
            MailStore.AddTagRule(tag.Id, p);
            foreach (var m in MailStore.MessagesMatching(p))
            {
                if (tagged.Add((m.AccountId, m.MessageKey)))
                {
                    MailStore.AddMessageTag(m.AccountId, m.MessageKey, tag.Id);
                    auto++;
                }
            }
        }
        return auto;
    }

    /// <summary>Removes a tag; the sender's rule is dropped too so it stops being applied automatically.</summary>
    public static void Untag(IEnumerable<MessageRecord> messages, TagInfo tag)
    {
        foreach (var m in messages)
        {
            MailStore.RemoveMessageTag(m.AccountId, m.MessageKey, tag.Id);
            var sender = m.FromAddress.ToLowerInvariant();
            MailStore.RemoveTagRule(tag.Id, sender);
            foreach (var rule in MailStore.GetTagRules().Where(r => r.TagId == tag.Id && r.Pattern.StartsWith('@') && Matches(r.Pattern, sender)))
                MailStore.RemoveTagRule(tag.Id, rule.Pattern);
        }
    }
}
