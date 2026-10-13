using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class ReaderViewModel : ObservableObject
{
    [ObservableProperty] private MessageItemViewModel? _item;
    [ObservableProperty] private MimeMessage? _mime;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _fromName = "";
    [ObservableProperty] private string _fromAddress = "";
    [ObservableProperty] private string _toLine = "";
    [ObservableProperty] private string _ccLine = "";
    [ObservableProperty] private string _dateLine = "";
    [ObservableProperty] private string? _spamReasons;
    [ObservableProperty] private string? _phishingWarning;
    [ObservableProperty] private bool _remoteContentBlocked;
    [ObservableProperty] private bool _canUnsubscribe;
    [ObservableProperty] private bool _isFromKnownContact;

    public ObservableCollection<AttachmentInfo> Attachments { get; } = new();
    public ObservableCollection<TagItemViewModel> MessageTags { get; } = new();

    /// <summary>Refreshes the tag chips shown under the subject.</summary>
    public void RefreshTags(IEnumerable<TagItemViewModel> allTags)
    {
        MessageTags.Clear();
        if (Item == null) return;
        var ids = QuickMail.Data.MailStore.TagsForMessages([Item.Record]).GetValueOrDefault((Item.Record.AccountId, Item.Record.MessageKey)) ?? [];
        foreach (var t in allTags.Where(t => ids.Contains(t.Tag.Id))) MessageTags.Add(t);
    }

    public bool HasMessage => Item != null;
    public bool AllowRemoteOnce { get; set; }

    partial void OnItemChanged(MessageItemViewModel? value) => OnPropertyChanged(nameof(HasMessage));

    public void Begin(MessageItemViewModel item)
    {
        Item = item;
        Mime = null;
        Error = null;
        IsLoading = true;
        AllowRemoteOnce = false;
        var r = item.Record;
        Subject = item.Subject;
        FromName = string.IsNullOrWhiteSpace(r.FromName) ? r.FromAddress : r.FromName;
        FromAddress = r.FromAddress;
        ToLine = r.ToList;
        CcLine = r.CcList;
        DateLine = r.LocalDate.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
        SpamReasons = r.IsSpam || r.Folder == FolderKind.Junk ? (string.IsNullOrWhiteSpace(r.SpamReasons) ? "This message is in your spam folder." : r.SpamReasons) : null;
        PhishingWarning = null;
        RemoteContentBlocked = false;
        CanUnsubscribe = !string.IsNullOrWhiteSpace(r.ListUnsubscribe);
        IsFromKnownContact = SpamEngine.IsKnownCorrespondent(r.FromAddress);
        Attachments.Clear();
    }

    public void Load(MimeMessage mime)
    {
        Mime = mime;
        IsLoading = false;
        Attachments.Clear();
        int i = 0;
        foreach (var a in mime.Attachments)
        {
            var name = a is MimePart p ? p.FileName : (a as MessagePart)?.Message?.Subject + ".eml";
            long size = 0;
            if (a is MimePart part && part.Content?.Stream is { CanSeek: true } s) size = s.Length;
            Attachments.Add(new AttachmentInfo { FileName = string.IsNullOrWhiteSpace(name) ? $"attachment-{i + 1}" : name, Size = size, Index = i });
            i++;
        }
        if (mime.HtmlBody is { } html && !IsFromKnownContact) PhishingWarning = SpamEngine.FindDeceptiveLinks(html);
        if (string.IsNullOrEmpty(ToLine)) ToLine = mime.To.ToString();
        if (string.IsNullOrEmpty(CcLine)) CcLine = mime.Cc.ToString();
    }

    public void Clear()
    {
        Item = null;
        Mime = null;
        Error = null;
        IsLoading = false;
        Attachments.Clear();
    }
}
