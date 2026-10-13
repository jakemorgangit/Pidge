using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Pidge.Helpers;
using Pidge.Models;

namespace Pidge.ViewModels;

public partial class AccountCardViewModel : ObservableObject
{
    [ObservableProperty] private MailAccount _account;
    [ObservableProperty] private int _unread;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isSelected;

    public AccountCardViewModel(MailAccount account) => _account = account;

    public string Name => string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.Email : Account.DisplayName;
    public string Email => Account.Email;
    public SolidColorBrush ColorBrush => Palette.FromHex(Account.Color);
    public string Initials => MessageItemViewModel.MakeInitials(Name);

    partial void OnAccountChanged(MailAccount value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(Initials));
    }
}

public partial class MessageItemViewModel : ObservableObject
{
    public MessageRecord Record { get; private set; }
    public MailAccount? Account { get; }

    [ObservableProperty] private bool _isRead;
    [ObservableProperty] private bool _isFlagged;
    [ObservableProperty] private bool _isAnswered;

    public MessageItemViewModel(MessageRecord record, MailAccount? account)
    {
        Record = record;
        Account = account;
        _isRead = record.IsRead;
        _isFlagged = record.IsFlagged;
        _isAnswered = record.IsAnswered;
    }

    public long Id => Record.Id;

    public string Sender => Record.Folder is FolderKind.Sent or FolderKind.Drafts
        ? "To: " + FirstRecipient(Record.ToList)
        : !string.IsNullOrWhiteSpace(Record.FromName) ? Record.FromName
        : !string.IsNullOrWhiteSpace(Record.FromAddress) ? Record.FromAddress : "(unknown sender)";

    public string Subject => string.IsNullOrWhiteSpace(Record.Subject) ? "(no subject)" : Record.Subject;
    public string Preview => Record.Preview.Replace('\r', ' ').Replace('\n', ' ');
    public string Initials => MakeInitials(Sender.StartsWith("To: ") ? Sender[4..] : Sender);
    public SolidColorBrush AvatarBrush => Palette.ForText(Record.FromAddress.Length > 0 ? Record.FromAddress : Sender);
    public SolidColorBrush AccountBrush => Palette.FromHex(Account?.Color);
    public string AccountName => Account == null ? "" : string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.Email : Account.DisplayName;
    public bool HasAttachments => Record.HasAttachments;
    public bool IsSpam => Record.IsSpam;

    public string? CategoryLabel => Record.Category switch
    {
        MailCategory.Newsletter => "Newsletter",
        MailCategory.Notification => "Notification",
        MailCategory.Social => "Social",
        MailCategory.Promotion => "Promo",
        _ => null
    };

    public string DateText
    {
        get
        {
            var d = Record.LocalDate;
            var today = DateTime.Today;
            if (d.Date == today) return d.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (d.Date == today.AddDays(-1)) return "Yesterday";
            if (d > today.AddDays(-6)) return d.ToString("ddd", CultureInfo.CurrentCulture);
            if (d.Year == today.Year) return d.ToString("d MMM", CultureInfo.CurrentCulture);
            return d.ToString("d MMM yy", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Ids of the tags on this message, and chips to draw for them.</summary>
    public IReadOnlyList<long> TagIds { get; private set; } = [];
    [ObservableProperty] private IReadOnlyList<TagChip> _tagChips = [];

    public void SetTags(IReadOnlyList<long> ids, IReadOnlyDictionary<long, TagItemViewModel> tags)
    {
        if (ids.SequenceEqual(TagIds) && TagChips.Count == ids.Count(tags.ContainsKey)
            && TagChips.Select(t => t.Name).SequenceEqual(ids.Where(tags.ContainsKey).Select(i => tags[i].Name))) return;
        TagIds = ids;
        TagChips = ids.Where(tags.ContainsKey).Select(i => new TagChip(tags[i].Name, tags[i].Brush)).ToList();
    }

    public void Update(MessageRecord fresh)
    {
        Record = fresh;
        IsRead = fresh.IsRead;
        IsFlagged = fresh.IsFlagged;
        IsAnswered = fresh.IsAnswered;
    }

    public static string MakeInitials(string text)
    {
        var parts = text.Split([' ', '.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => char.IsLetterOrDigit(p[0])).ToArray();
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..1].ToUpperInvariant();
        return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }

    private static string FirstRecipient(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return "(no recipients)";
        if (MimeKit.InternetAddressList.TryParse(list, out var parsed) && parsed.Mailboxes.FirstOrDefault() is { } mb)
            return string.IsNullOrWhiteSpace(mb.Name) ? mb.Address : mb.Name;
        return list;
    }
}

public record TagChip(string Name, SolidColorBrush Brush);

public partial class TagItemViewModel : ObservableObject
{
    public TagInfo Tag { get; private set; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _count;

    public TagItemViewModel(TagInfo tag)
    {
        Tag = tag;
        _count = tag.Count;
    }

    public string Name => Tag.Name;
    public SolidColorBrush Brush => Palette.FromHex(Tag.Color);

    public void Update(TagInfo fresh)
    {
        Tag = fresh;
        Count = fresh.Count;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Brush));
    }
}

/// <summary>A row in the Folders drop-down: an account header, a folder, or a short note.</summary>
public class FolderChoice
{
    public long AccountId { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int Depth { get; init; }
    public int Unread { get; init; }
    public bool Selectable { get; init; }
    public bool IsHeader { get; init; }
    public bool IsNote { get; init; }
    public bool IsCurrent { get; init; }
    public SolidColorBrush? AccountBrush { get; init; }
    public System.Windows.Thickness Indent => new(12 + Depth * 16, 0, 0, 0);

    public static FolderChoice Header(AccountCardViewModel card) =>
        new() { AccountId = card.Account.Id, Name = card.Name, IsHeader = true, AccountBrush = card.ColorBrush };

    public static FolderChoice Note(long accountId, string text) => new() { AccountId = accountId, Name = text, IsNote = true };

    public static FolderChoice For(FolderInfo f, AccountCardViewModel card, bool current) => new()
    {
        AccountId = f.AccountId, Path = f.Path, Name = f.Name, Depth = f.Depth, Unread = f.Unread,
        Selectable = f.Selectable, AccountBrush = card.ColorBrush, IsCurrent = current
    };
}