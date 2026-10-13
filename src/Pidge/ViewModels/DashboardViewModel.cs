using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Pidge.Data;
using Pidge.Helpers;
using Pidge.Models;

namespace Pidge.ViewModels;

public record KpiTile(string Label, string Value, string Hint, string Glyph, SolidColorBrush Accent);

public record AccountRow(AccountCardViewModel Card, int Total, int Unread, int Today, int Spam, int Flagged, double UnreadBar)
{
    public string Name => Card.Name;
    public SolidColorBrush Brush => Card.ColorBrush;
}

public record DayBar(string Label, string Tooltip, int Count, double Height, bool IsToday);

public record CategoryRow(string Label, int Count, double Fraction, SolidColorBrush Brush)
{
    public string Percent => $"{Fraction:P0}";
}

public record SenderRow(string Name, string Address, int Total, double ReadRate, bool IsNoisy)
{
    public string Detail => $"{Total} emails · {ReadRate:P0} opened";
    public string Initials => MessageItemViewModel.MakeInitials(Name);
    public SolidColorBrush Avatar => Palette.ForText(Address);
}

public partial class DashboardViewModel : ObservableObject
{
    public ObservableCollection<KpiTile> Kpis { get; } = new();
    public ObservableCollection<AccountRow> AccountRows { get; } = new();
    public ObservableCollection<DayBar> Days { get; } = new();
    public ObservableCollection<CategoryRow> Categories { get; } = new();
    public ObservableCollection<SenderRow> TopSenders { get; } = new();
    public ObservableCollection<SenderRow> NoisySenders { get; } = new();
    public ObservableCollection<MessageItemViewModel> NeedsAttention { get; } = new();

    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _maxDay;

    private const double BarMaxHeight = 120;

    public void Refresh(IReadOnlyList<AccountCardViewModel> cards)
    {
        var stats = MailStore.AccountStats().ToDictionary(s => s.AccountId);
        int unread = stats.Values.Sum(s => s.Unread);
        int today = stats.Values.Sum(s => s.Today);
        int flagged = stats.Values.Sum(s => s.Flagged);
        int spamNow = stats.Values.Sum(s => s.Spam);
        long spamCaught = MailStore.GetCounter("spamCaught");
        long blocked = MailStore.GetCounter("blocked");
        var volume = MailStore.DailyVolume(14);
        int week = volume.Where(v => DateTime.Parse(v.Day, CultureInfo.InvariantCulture) > DateTime.Today.AddDays(-7)).Sum(v => v.Count);

        var hour = DateTime.Now.Hour;
        Greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        Summary = unread == 0
            ? $"Inbox zero across {cards.Count} account{(cards.Count == 1 ? "" : "s")}. Nice."
            : $"{unread:N0} unread across {cards.Count(c => stats.TryGetValue(c.Account.Id, out var s) && s.Unread > 0)} of {cards.Count} accounts · {today:N0} arrived today";

        Kpis.Clear();
        Kpis.Add(new KpiTile("Unread", unread.ToString("N0"), "across all inboxes", "", Palette.FromHex("#7C6CFF")));
        Kpis.Add(new KpiTile("Today", today.ToString("N0"), $"{week:N0} in the last 7 days", "", Palette.FromHex("#4CC9F0")));
        Kpis.Add(new KpiTile("Flagged", flagged.ToString("N0"), "waiting on you", "", Palette.FromHex("#FFB648")));
        Kpis.Add(new KpiTile("Spam caught", spamCaught.ToString("N0"), $"{spamNow:N0} awaiting review", "", Palette.FromHex("#FF5D73")));
        Kpis.Add(new KpiTile("Blocked", blocked.ToString("N0"), "messages auto-binned", "", Palette.FromHex("#36D399")));

        AccountRows.Clear();
        int maxUnread = Math.Max(1, stats.Values.Select(s => s.Unread).DefaultIfEmpty(0).Max());
        foreach (var card in cards)
        {
            stats.TryGetValue(card.Account.Id, out var s);
            s ??= new AccountStat { AccountId = card.Account.Id };
            AccountRows.Add(new AccountRow(card, s.Total, s.Unread, s.Today, s.Spam, s.Flagged, (double)s.Unread / maxUnread));
        }

        Days.Clear();
        var byDay = volume.ToDictionary(v => v.Day, v => v.Count);
        MaxDay = Math.Max(1, volume.Select(v => v.Count).DefaultIfEmpty(0).Max());
        for (int i = 13; i >= 0; i--)
        {
            var d = DateTime.Today.AddDays(-i);
            byDay.TryGetValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var n);
            Days.Add(new DayBar(i == 0 ? "Today" : d.ToString("ddd", CultureInfo.CurrentCulture)[..2], $"{d:ddd d MMM}: {n} emails", n,
                Math.Max(2, BarMaxHeight * n / MaxDay), i == 0));
        }

        Categories.Clear();
        var cats = MailStore.Categories(30);
        int catTotal = Math.Max(1, cats.Sum(c => c.Count));
        foreach (var c in cats.OrderByDescending(c => c.Count))
        {
            var (label, color) = c.Category switch
            {
                MailCategory.Personal => ("People", "#7C6CFF"),
                MailCategory.Newsletter => ("Newsletters", "#4CC9F0"),
                MailCategory.Notification => ("Notifications", "#A0A8BA"),
                MailCategory.Social => ("Social", "#F472B6"),
                _ => ("Promotions", "#FFB648")
            };
            Categories.Add(new CategoryRow(label, c.Count, (double)c.Count / catTotal, Palette.FromHex(color)));
        }

        TopSenders.Clear();
        foreach (var s in MailStore.TopSenders(30, 8))
            TopSenders.Add(new SenderRow(string.IsNullOrWhiteSpace(s.FromName) ? s.FromAddress : s.FromName, s.FromAddress, s.Total, s.ReadRate, false));

        NoisySenders.Clear();
        foreach (var s in MailStore.NoisySenders(6))
            NoisySenders.Add(new SenderRow(string.IsNullOrWhiteSpace(s.FromName) ? s.FromAddress : s.FromName, s.FromAddress, s.Total, s.ReadRate, true));

        NeedsAttention.Clear();
        var byId = cards.ToDictionary(c => c.Account.Id, c => c.Account);
        foreach (var m in MailStore.NeedsAttention(8))
            NeedsAttention.Add(new MessageItemViewModel(m, byId.GetValueOrDefault(m.AccountId)));
    }
}
