using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using QuickMail.Data;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MailEngine _engine = MailEngine.Instance;
    private readonly DispatcherTimer _reloadTimer;
    private readonly DispatcherTimer _searchTimer;
    private int _limit = 500;
    private int _readerToken;
    private HashSet<string> _trusted = MailStore.GetTrusted();

    public ObservableCollection<AccountCardViewModel> Accounts { get; } = new();
    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();
    public DashboardViewModel Dashboard { get; } = new();
    public ReaderViewModel Reader { get; } = new();

    [ObservableProperty] private bool _isDashboard;
    [ObservableProperty] private AccountCardViewModel? _selectedAccount;
    [ObservableProperty] private FolderKind _folder = FolderKind.Inbox;
    [ObservableProperty] private MessageFilter _filter = MessageFilter.All;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private MessageItemViewModel? _selectedMessage;
    [ObservableProperty] private int _totalUnread;
    [ObservableProperty] private int _spamCount;
    [ObservableProperty] private int _syncingCount;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _darkReading = AppSettings.Current.DarkReading;
    [ObservableProperty] private bool _canLoadMore;
    /// <summary>Server path of the open custom folder (when <see cref="Folder"/> is Custom).</summary>
    [ObservableProperty] private string _folderPath = "";
    /// <summary>The account the open custom folder belongs to.</summary>
    [ObservableProperty] private long? _folderAccountId;
    /// <summary>When set, the list shows everything carrying this tag.</summary>
    [ObservableProperty] private TagItemViewModel? _selectedTag;
    [ObservableProperty] private double _uiScale = Math.Clamp(AppSettings.Current.UiScale, MinScale, MaxScale);

    public const double MinScale = 0.8, MaxScale = 1.6;

    public ObservableCollection<FolderChoice> FolderChoices { get; } = new();
    public ObservableCollection<TagItemViewModel> Tags { get; } = new();

    /// <summary>Items currently selected in the message list (multi-select).</summary>
    public IList<MessageItemViewModel> SelectedMessages { get; set; } = [];

    /// <summary>The view renders this HTML in the reading pane.</summary>
    public event Action<string>? RenderHtml;
    /// <summary>Ask the view to focus/select a specific message.</summary>
    public event Action<MessageItemViewModel>? FocusMessage;

    public bool IsUnified => SelectedAccount == null;
    public bool IsDrafts => Folder == FolderKind.Drafts;
    public bool IsCustomFolder => Folder == FolderKind.Custom && SelectedTag == null;
    public bool IsTagView => SelectedTag != null;
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasTags => Tags.Count > 0;
    public bool IsSyncing => SyncingCount > 0;
    public string ZoomText => $"{UiScale:P0}";

    public string ScopeTitle => IsTagView ? SelectedTag!.Name
        : IsCustomFolder && IsUnified ? Accounts.FirstOrDefault(a => a.Account.Id == FolderAccountId)?.Name ?? "Folder"
        : IsUnified ? "All inboxes" : SelectedAccount!.Name;

    public string FolderTitle => IsTagView ? "Tagged mail across all accounts" : Folder switch
    {
        FolderKind.Inbox => "Inbox", FolderKind.Sent => "Sent", FolderKind.Drafts => "Drafts",
        FolderKind.Archive => "Archive", FolderKind.Junk => "Spam", FolderKind.Custom => FolderPath, _ => "Bin"
    };

    public string EmptyText => !HasAccounts ? "Add an email account to get started."
        : !string.IsNullOrWhiteSpace(SearchText) ? "No messages match your search."
        : IsTagView ? "Nothing tagged yet. Tag a message and QuickMail tags similar mail for you."
        : Filter == MessageFilter.Spam ? "No suspected spam. 🎉"
        : Folder == FolderKind.Inbox && Filter == MessageFilter.All ? "You're all caught up."
        : "Nothing here.";

    /// <summary>The account whose mail is listed, for queries and syncing (null = every account).</summary>
    private long? ListAccountId => IsCustomFolder ? FolderAccountId : SelectedAccount?.Account.Id;

    public MainViewModel()
    {
        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); ReloadList(); RefreshCounts(); if (IsDashboard) Dashboard.Refresh(Accounts); };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ReloadList(); };

        foreach (var a in MailStore.GetAccounts()) Accounts.Add(new AccountCardViewModel(a));

        _engine.FolderChanged += (id, kind, path) => OnUi(() =>
        {
            bool showing = !IsTagView && kind == Folder && path == (IsCustomFolder ? FolderPath : "")
                           && (ListAccountId == null || ListAccountId == id);
            if (showing || IsTagView) _reloadTimer.Start();
            else if (kind == FolderKind.Inbox) { RefreshCounts(); if (IsDashboard) _reloadTimer.Start(); }
        });
        _engine.FoldersChanged += id => OnUi(() => { if (FolderChoices.Count > 0) LoadFolderChoices(); });
        ConfigSync.Applied += () => OnUi(() => { ReloadAccounts(); Status = ConfigSync.Status; });

        foreach (var t in MailStore.GetTags()) Tags.Add(new TagItemViewModel(t));
        _engine.SyncState += (id, syncing, error) => OnUi(() =>
        {
            var card = Accounts.FirstOrDefault(c => c.Account.Id == id);
            if (card == null) return;
            card.IsSyncing = syncing;
            if (!syncing) card.Error = error;
            SyncingCount = Accounts.Count(c => c.IsSyncing);
            Status = error != null ? $"{card.Name}: {error}" : IsSyncing ? "Syncing…" : $"Updated {DateTime.Now:HH:mm}";
        });
        _engine.NewMail += (id, count, newest) => OnUi(() => NewMailArrived?.Invoke(id, count, newest));

        ReloadList();
        RefreshCounts();
        if (!HasAccounts) IsDashboard = false;
    }

    public event Action<long, int, MessageRecord?>? NewMailArrived;

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // ---------------- Navigation ----------------
    partial void OnSelectedAccountChanged(AccountCardViewModel? value)
    {
        foreach (var c in Accounts) c.IsSelected = c == value;
        OnPropertyChanged(nameof(IsUnified));
        OnPropertyChanged(nameof(ScopeTitle));
    }

    partial void OnFolderChanged(FolderKind value)
    {
        if (value != FolderKind.Custom) { FolderPath = ""; FolderAccountId = null; }
        if (SelectedTag != null) SelectedTag = null;
        if (value != FolderKind.Inbox && Filter == MessageFilter.Spam) Filter = MessageFilter.All;
        NotifyLocation();
        _limit = 500;
        ReloadList();
        _ = SyncScopeAsync();
    }

    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        foreach (var t in Tags) t.IsSelected = t == value;
        NotifyLocation();
    }

    private void NotifyLocation()
    {
        OnPropertyChanged(nameof(FolderTitle));
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(IsDrafts));
        OnPropertyChanged(nameof(IsCustomFolder));
        OnPropertyChanged(nameof(IsTagView));
    }

    partial void OnUiScaleChanged(double value)
    {
        AppSettings.Current.UiScale = value;
        AppSettings.Save();
        OnPropertyChanged(nameof(ZoomText));
    }

    [RelayCommand] private void ZoomIn() => UiScale = Math.Min(MaxScale, Math.Round(UiScale + 0.1, 1));
    [RelayCommand] private void ZoomOut() => UiScale = Math.Max(MinScale, Math.Round(UiScale - 0.1, 1));
    [RelayCommand] private void ZoomReset() => UiScale = 1.0;

    // ---------------- Folders ----------------
    /// <summary>Fills the Folders drop-down: the selected account's folders, or every account's (grouped) in All inboxes.</summary>
    public void LoadFolderChoices()
    {
        FolderChoices.Clear();
        var cards = IsUnified ? Accounts.ToList() : [SelectedAccount!];
        foreach (var card in cards)
        {
            var folders = MailStore.GetFolders(card.Account.Id);
            if (folders.Count == 0 && !MailEngine.Offline) _ = Task.Run(() => _engine.RefreshFoldersAsync(card.Account.Id));
            if (cards.Count > 1) FolderChoices.Add(FolderChoice.Header(card));
            if (folders.Count == 0)
            {
                FolderChoices.Add(FolderChoice.Note(card.Account.Id, MailEngine.Offline ? "No folders" : "Looking for folders…"));
                continue;
            }
            foreach (var f in folders) FolderChoices.Add(FolderChoice.For(f, card, FolderPath == f.Path && FolderAccountId == f.AccountId));
        }
    }

    [RelayCommand]
    private void OpenFolder(FolderChoice choice)
    {
        if (!choice.Selectable) return;
        IsDashboard = false;
        FolderAccountId = choice.AccountId;
        FolderPath = choice.Path;
        if (Folder == FolderKind.Custom && SelectedTag == null)
        {
            NotifyLocation();
            _limit = 500;
            ReloadList();
            _ = SyncScopeAsync();
        }
        else
        {
            SelectedTag = null;
            Folder = FolderKind.Custom; // triggers reload + sync (keeps FolderPath because it's Custom)
        }
    }

    [RelayCommand]
    private async Task MoveToFolderAsync()
    {
        var items = Targets();
        if (items.Count == 0) return;
        var accountId = items[0].Record.AccountId;
        if (items.Any(i => i.Record.AccountId != accountId))
        {
            Ui.Info("Pick messages from one account at a time to move them to a folder.", "Move to folder");
            return;
        }
        var choice = Ui.PickFolder(accountId);
        if (choice == null) return;
        RemoveAndAdvance(items);
        await _engine.MoveAsync(items.Select(i => i.Record).ToList(), choice.Value.kind, choice.Value.path);
        Status = $"Moved {items.Count} message{(items.Count == 1 ? "" : "s")}";
    }

    // ---------------- Tags ----------------
    private void ReloadTags()
    {
        var fresh = MailStore.GetTags();
        foreach (var t in Tags.Where(t => fresh.All(f => f.Id != t.Tag.Id)).ToList()) Tags.Remove(t);
        foreach (var f in fresh)
        {
            var vm = Tags.FirstOrDefault(t => t.Tag.Id == f.Id);
            if (vm == null) Tags.Add(new TagItemViewModel(f));
            else vm.Update(f);
        }
        if (SelectedTag != null && !Tags.Contains(SelectedTag)) SelectedTag = null;
        OnPropertyChanged(nameof(HasTags));
    }

    [RelayCommand]
    private void ShowTag(TagItemViewModel tag)
    {
        IsDashboard = false;
        SelectedTag = tag;
        _limit = 500;
        ReloadList();
    }

    [RelayCommand]
    private void ToggleTag(TagItemViewModel tag)
    {
        var items = Targets();
        if (items.Count == 0) return;
        if (items.All(i => i.TagIds.Contains(tag.Tag.Id)))
        {
            TagEngine.Untag(items.Select(i => i.Record), tag.Tag);
            Status = $"Removed \"{tag.Name}\". QuickMail won't tag mail from {(items.Count == 1 ? "this sender" : "these senders")} automatically any more.";
        }
        else
        {
            var auto = TagEngine.Tag(items.Select(i => i.Record), tag.Tag);
            Status = $"Tagged \"{tag.Name}\"" + (auto > 0 ? $" · {auto} similar message{(auto == 1 ? "" : "s")} tagged automatically" : "") +
                     ". Future mail from the same sender will be tagged too.";
        }
        ReloadTags();
        ReloadList();
        Reader.RefreshTags(Tags);
    }

    [RelayCommand]
    private void NewTag()
    {
        var name = Ui.AskText("New tag", "Name the tag. Anything you tag teaches QuickMail to tag similar mail from that sender automatically.", "e.g. Invoices, Clients, Travel");
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = Tags.FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var tag = MailStore.CreateTag(name, TagEngine.Palette[Tags.Count % TagEngine.Palette.Length]);
            ReloadTags();
            existing = Tags.First(t => t.Tag.Id == tag.Id);
        }
        if (Targets().Count > 0 && !IsDashboard) ToggleTag(existing);
    }

    [RelayCommand]
    private void DeleteTag(TagItemViewModel tag)
    {
        if (!Ui.Confirm($"Delete the tag \"{tag.Name}\"? Messages aren't deleted, they just lose the tag.", "Delete tag")) return;
        MailStore.DeleteTag(tag.Tag.Id);
        ReloadTags();
        ReloadList();
    }

    [RelayCommand]
    private void RenameTag(TagItemViewModel tag)
    {
        var name = Ui.AskText("Rename tag", "New name for this tag:", tag.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        tag.Tag.Name = name.Trim();
        MailStore.UpdateTag(tag.Tag);
        ReloadTags();
        ReloadList();
    }

    partial void OnFilterChanged(MessageFilter value) => ReloadList();
    partial void OnSearchTextChanged(string value) { _searchTimer.Stop(); _searchTimer.Start(); }
    partial void OnSyncingCountChanged(int value) => OnPropertyChanged(nameof(IsSyncing));

    partial void OnDarkReadingChanged(bool value)
    {
        AppSettings.Current.DarkReading = value;
        AppSettings.Save();
        Render();
    }

    [RelayCommand]
    private void ShowDashboard()
    {
        Dashboard.Refresh(Accounts);
        IsDashboard = true;
    }

    [RelayCommand]
    private void ShowUnified()
    {
        IsDashboard = false;
        if (SelectedAccount == null && !IsCustomFolder && !IsTagView) return;
        SelectedAccount = null;
        if (LeaveFolderOrTag()) return;
        ReloadList();
        _ = SyncScopeAsync();
    }

    [RelayCommand]
    private void SelectAccount(AccountCardViewModel card)
    {
        IsDashboard = false;
        if (SelectedAccount == card && !IsCustomFolder && !IsTagView) return;
        SelectedAccount = card;
        _limit = 500;
        if (LeaveFolderOrTag()) return;
        ReloadList();
        _ = SyncScopeAsync();
    }

    /// <summary>Picking an account or All inboxes leaves a custom folder / tag view and goes back to the inbox.</summary>
    private bool LeaveFolderOrTag()
    {
        if (!IsCustomFolder && !IsTagView) return false;
        SelectedTag = null;
        if (Folder != FolderKind.Inbox) { Folder = FolderKind.Inbox; return true; }
        NotifyLocation();
        return false;
    }

    public void SelectAccountByIndex(int index)
    {
        if (index >= 0 && index < Accounts.Count) SelectAccount(Accounts[index]);
    }

    [RelayCommand]
    private void SetFolder(FolderKind kind)
    {
        IsDashboard = false;
        Folder = kind;
    }

    [RelayCommand]
    private void ShowSpamReview()
    {
        IsDashboard = false;
        Folder = FolderKind.Inbox;
        Filter = MessageFilter.Spam;
    }

    /// <summary>Open a message from the dashboard: jump to its inbox in unified view and select it.</summary>
    [RelayCommand]
    private void OpenFromDashboard(MessageItemViewModel item)
    {
        IsDashboard = false;
        SelectedAccount = null;
        Folder = FolderKind.Inbox;
        Filter = MessageFilter.All;
        SearchText = "";
        ReloadList();
        var target = Messages.FirstOrDefault(m => m.Id == item.Id);
        if (target != null)
        {
            SelectedMessage = target;
            FocusMessage?.Invoke(target);
        }
    }

    [RelayCommand]
    private void ShowSender(string address)
    {
        IsDashboard = false;
        SelectedAccount = null;
        Folder = FolderKind.Inbox;
        Filter = MessageFilter.All;
        SearchText = address;
        ReloadList();
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        foreach (var id in (IsUnified ? Accounts.Select(a => a.Account.Id) : [SelectedAccount!.Account.Id]).ToList())
            _ = Task.Run(() => _engine.RefreshFoldersAsync(id));
        return SyncScopeAsync();
    }

    private Task SyncScopeAsync()
    {
        if (IsTagView) return Task.Run(() => _engine.SyncAllAsync(FolderKind.Inbox));
        var kind = Folder;
        var path = IsCustomFolder ? FolderPath : "";
        var account = ListAccountId;
        return Task.Run(() => account == null ? _engine.SyncAllAsync(kind) : _engine.SyncFolderAsync(account.Value, kind, path));
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        _limit += 500;
        var before = Messages.Count;
        ReloadList();
        if (Messages.Count > before) return;

        Status = "Fetching older messages…";
        if (IsTagView) return;
        var ids = ListAccountId == null ? Accounts.Select(a => a.Account.Id).ToList() : [ListAccountId.Value];
        var kind = Folder;
        var path = IsCustomFolder ? FolderPath : "";
        int total = 0;
        foreach (var id in ids)
        {
            try { total += await Task.Run(() => _engine.LoadOlderAsync(id, kind, path)); }
            catch (Exception ex) { Status = AccountSession.Friendly(ex); }
        }
        Status = total == 0 ? "No older messages on the server." : $"Loaded {total} older messages.";
    }

    // ---------------- List ----------------
    public void ReloadList()
    {
        var byId = Accounts.ToDictionary(a => a.Account.Id, a => a.Account);
        var records = (IsTagView
                ? MailStore.QueryTagged(SelectedTag!.Tag.Id, Filter, SearchText, _limit)
                : MailStore.QueryMessages(ListAccountId, Folder, Filter, SearchText, _limit, IsCustomFolder ? FolderPath : ""))
            .Where(r => byId.ContainsKey(r.AccountId)).ToList();
        CanLoadMore = string.IsNullOrWhiteSpace(SearchText) && Filter != MessageFilter.Spam && !IsTagView && records.Count > 0;

        var tagMap = MailStore.TagsForMessages(records);
        var tagById = Tags.ToDictionary(t => t.Tag.Id);

        var existing = Messages.ToDictionary(m => m.Id);
        var result = new List<MessageItemViewModel>(records.Count);
        foreach (var r in records)
        {
            if (!existing.TryGetValue(r.Id, out var vm)) vm = new MessageItemViewModel(r, byId[r.AccountId]);
            else vm.Update(r);
            vm.SetTags(tagMap.GetValueOrDefault((r.AccountId, r.MessageKey)) ?? [], tagById);
            result.Add(vm);
        }

        // Merge in place so scroll position and selection survive background syncs.
        var keep = result.Select(r => r.Id).ToHashSet();
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (!keep.Contains(Messages[i].Id)) Messages.RemoveAt(i);
        for (int i = 0; i < result.Count; i++)
        {
            if (i < Messages.Count && ReferenceEquals(Messages[i], result[i])) continue;
            var at = Messages.IndexOf(result[i]);
            if (at >= 0) Messages.Move(at, i);
            else Messages.Insert(i, result[i]);
        }

        if (SelectedMessage != null && !keep.Contains(SelectedMessage.Id)) SelectedMessage = null;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasAccounts));
    }

    private void RefreshCounts()
    {
        var unread = MailStore.UnreadByAccount();
        foreach (var c in Accounts) c.Unread = unread.GetValueOrDefault(c.Account.Id);
        TotalUnread = unread.Values.Sum();
        SpamCount = MailStore.CountSpam(SelectedAccount?.Account.Id);
    }

    // ---------------- Reader ----------------
    partial void OnSelectedMessageChanged(MessageItemViewModel? value) => _ = LoadReaderAsync(value);

    private async Task LoadReaderAsync(MessageItemViewModel? item)
    {
        var token = ++_readerToken;
        if (item == null)
        {
            Reader.Clear();
            RenderHtml?.Invoke("");
            return;
        }

        Reader.Begin(item);
        Reader.RefreshTags(Tags);
        RenderHtml?.Invoke("");
        try
        {
            var mime = await Task.Run(() => _engine.GetFullMessageAsync(item.Record));
            if (token != _readerToken) return;
            Reader.Load(mime);
            Render();

            if (!item.IsRead && item.Record.Folder != FolderKind.Drafts)
            {
                item.IsRead = true;
                await _engine.SetReadAsync([item.Record], true);
                RefreshCounts();
            }
        }
        catch (Exception ex)
        {
            if (token != _readerToken) return;
            Reader.IsLoading = false;
            Reader.Error = "Couldn't open this message: " + AccountSession.Friendly(ex);
        }
    }

    private void Render()
    {
        if (Reader.Mime == null) return;
        bool trusted = _trusted.Contains(Reader.FromAddress) || Reader.IsFromKnownContact;
        bool allowRemote = !AppSettings.Current.BlockRemoteImages || trusted || Reader.AllowRemoteOnce;
        if (Reader.Item?.Record.IsSpam == true || Reader.Item?.Record.Folder == FolderKind.Junk) allowRemote = Reader.AllowRemoteOnce;
        Reader.RemoteContentBlocked = !allowRemote && HtmlTools.HasRemoteContent(Reader.Mime);
        RenderHtml?.Invoke(HtmlTools.BuildReaderPage(Reader.Mime, allowRemote, DarkReading));
    }

    [RelayCommand]
    private void LoadImages()
    {
        Reader.AllowRemoteOnce = true;
        Render();
    }

    [RelayCommand]
    private void AlwaysLoadImages()
    {
        if (string.IsNullOrEmpty(Reader.FromAddress)) return;
        MailStore.AddTrusted(Reader.FromAddress);
        _trusted = MailStore.GetTrusted();
        Reader.AllowRemoteOnce = true;
        Render();
    }

    [RelayCommand]
    private async Task SaveAttachmentAsync(AttachmentInfo att)
    {
        var part = AttachmentPart(att);
        if (part == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = att.FileName };
        if (dlg.ShowDialog() != true) return;
        await WriteAttachmentAsync(part, dlg.FileName);
    }

    [RelayCommand]
    private async Task OpenAttachmentAsync(AttachmentInfo att)
    {
        var part = AttachmentPart(att);
        if (part == null) return;
        var ext = Path.GetExtension(att.FileName).ToLowerInvariant();
        string[] risky = [".exe", ".bat", ".cmd", ".com", ".js", ".jse", ".vbs", ".vbe", ".ps1", ".scr", ".msi", ".hta", ".lnk", ".jar", ".wsf", ".reg", ".iso", ".img"];
        if (risky.Contains(ext) && !Ui.Confirm($"\"{att.FileName}\" can run programs on your PC. Only open it if you trust the sender.\n\nOpen anyway?", "Potentially dangerous attachment"))
            return;
        var dir = Path.Combine(Path.GetTempPath(), "QuickMail", "open", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ComposeViewModel.SafeFileName(att.FileName));
        await WriteAttachmentAsync(part, path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private MimeEntity? AttachmentPart(AttachmentInfo att) => Reader.Mime?.Attachments.ElementAtOrDefault(att.Index);

    private static async Task WriteAttachmentAsync(MimeEntity entity, string path)
    {
        await using var fs = File.Create(path);
        if (entity is MimePart part) await part.Content.DecodeToAsync(fs);
        else if (entity is MessagePart mp && mp.Message != null) await mp.Message.WriteToAsync(fs);
    }

    // ---------------- Actions ----------------
    private List<MessageItemViewModel> Targets()
    {
        var list = SelectedMessages.Count > 0 ? SelectedMessages.ToList() : [];
        if (list.Count == 0 && SelectedMessage != null) list.Add(SelectedMessage);
        return list;
    }

    /// <summary>Removes items from the list and selects the next one, for fast triage.</summary>
    private void RemoveAndAdvance(List<MessageItemViewModel> items)
    {
        if (items.Count == 0) return;
        int index = items.Select(i => Messages.IndexOf(i)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        foreach (var i in items) Messages.Remove(i);
        SelectedMessage = Messages.Count == 0 ? null : Messages[Math.Min(index, Messages.Count - 1)];
        if (SelectedMessage != null) FocusMessage?.Invoke(SelectedMessage);
        RefreshCounts();
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var items = Targets();
        if (items.Count == 0) return;
        if (Folder == FolderKind.Trash && !Ui.Confirm($"Permanently delete {items.Count} message{(items.Count == 1 ? "" : "s")}?", "Delete forever"))
            return;
        RemoveAndAdvance(items);
        await _engine.MoveAsync(items.Select(i => i.Record).ToList(), FolderKind.Trash);
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        var items = Targets();
        if (items.Count == 0 || Folder == FolderKind.Archive) return;
        RemoveAndAdvance(items);
        await _engine.MoveAsync(items.Select(i => i.Record).ToList(), FolderKind.Archive);
    }

    [RelayCommand]
    private async Task MoveToInboxAsync()
    {
        var items = Targets();
        if (items.Count == 0 || Folder == FolderKind.Inbox) return;
        RemoveAndAdvance(items);
        await _engine.MoveAsync(items.Select(i => i.Record).ToList(), FolderKind.Inbox);
    }

    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        var items = Targets();
        if (items.Count == 0) return;
        bool value = !items.All(i => i.IsFlagged);
        foreach (var i in items) i.IsFlagged = value;
        await _engine.SetFlaggedAsync(items.Select(i => i.Record).ToList(), value);
    }

    [RelayCommand]
    private async Task ToggleReadAsync()
    {
        var items = Targets();
        if (items.Count == 0) return;
        bool value = !items.All(i => i.IsRead);
        foreach (var i in items) i.IsRead = value;
        await _engine.SetReadAsync(items.Select(i => i.Record).ToList(), value);
        RefreshCounts();
    }

    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        var unread = Messages.Where(m => !m.IsRead).ToList();
        if (unread.Count == 0) return;
        foreach (var i in unread) i.IsRead = true;
        await _engine.SetReadAsync(unread.Select(i => i.Record).ToList(), true);
        RefreshCounts();
    }

    [RelayCommand]
    private async Task MarkSpamAsync()
    {
        var items = Targets();
        if (items.Count == 0) return;
        RemoveAndAdvance(items);
        await _engine.MarkSpamAsync(items.Select(i => i.Record).ToList());

        // Pattern recognition: offer to block a sender after repeated spam.
        var sender = items.Select(i => i.Record.FromAddress).FirstOrDefault(a => a.Length > 0);
        if (items.Count == 1 && sender != null && Ui.Confirm($"Marked as spam. QuickMail will learn from this.\n\nAlso block {sender} so future mail goes straight to the bin?", "Block sender?"))
            await BlockPatternAsync(sender);
        Status = "Marked as spam. The filter has learned from it.";
    }

    [RelayCommand]
    private async Task NotSpamAsync()
    {
        var items = Targets();
        if (items.Count == 0) return;
        RemoveAndAdvance(items);
        await _engine.NotSpamAsync(items.Select(i => i.Record).ToList());
        Status = "Moved back to the inbox. The filter has learned from it.";
    }

    [RelayCommand]
    private Task BlockSenderAsync() => BlockFromSelectionAsync(domain: false);

    [RelayCommand]
    private Task BlockDomainAsync() => BlockFromSelectionAsync(domain: true);

    private async Task BlockFromSelectionAsync(bool domain)
    {
        var address = Targets().Select(i => i.Record.FromAddress).FirstOrDefault(a => a.Contains('@'));
        if (address == null) return;
        var pattern = domain ? "@" + address[(address.IndexOf('@') + 1)..] : address;
        var what = domain ? $"everyone at {pattern[1..]}" : address;
        if (!Ui.Confirm($"Block {what}?\n\nExisting messages will be moved to the bin, and anything new is binned automatically. You can undo this in Settings → Blocked senders.", "Block"))
            return;
        await BlockPatternAsync(pattern);
    }

    [RelayCommand]
    private async Task BlockAddressAsync(string address)
    {
        if (!Ui.Confirm($"Block {address}? Existing and future messages go to the bin.", "Block")) return;
        await BlockPatternAsync(address);
        Dashboard.Refresh(Accounts);
    }

    private async Task BlockPatternAsync(string pattern)
    {
        var moved = await Task.Run(() => _engine.BlockAsync(pattern));
        ReloadList();
        RefreshCounts();
        Status = $"Blocked {pattern}" + (moved > 0 ? $" · {moved} message{(moved == 1 ? "" : "s")} moved to the bin" : "");
    }

    [RelayCommand]
    private void Unsubscribe()
    {
        var header = Reader.Item?.Record.ListUnsubscribe;
        if (string.IsNullOrWhiteSpace(header)) return;
        var links = Regex.Matches(header, "<([^>]+)>").Select(m => m.Groups[1].Value).ToList();
        var web = links.FirstOrDefault(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var mail = links.FirstOrDefault(l => l.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
        if (web != null)
        {
            if (Ui.Confirm($"Open the sender's unsubscribe page?\n\n{new Uri(web).Host}", "Unsubscribe"))
                Process.Start(new ProcessStartInfo(web) { UseShellExecute = true });
        }
        else if (mail != null)
        {
            var uri = new Uri(mail);
            var subject = Regex.Match(uri.Query, "subject=([^&]+)", RegexOptions.IgnoreCase) is { Success: true } m
                ? Uri.UnescapeDataString(m.Groups[1].Value) : "unsubscribe";
            var account = Reader.Item!.Account;
            var vm = ComposeViewModel.New(Accounts.Select(a => a.Account), account, Uri.UnescapeDataString(uri.AbsolutePath), subject);
            Ui.ShowCompose(vm);
        }
    }

    // ---------------- Compose ----------------
    private IEnumerable<MailAccount> AllAccounts => Accounts.Select(a => a.Account);

    [RelayCommand]
    private void Compose()
    {
        if (!HasAccounts) { AddAccount(); return; }
        Ui.ShowCompose(ComposeViewModel.New(AllAccounts, SelectedAccount?.Account ?? Reader.Item?.Account));
    }

    [RelayCommand] private void Reply() => OpenReply(false);
    [RelayCommand] private void ReplyAll() => OpenReply(true);

    private void OpenReply(bool all)
    {
        if (Reader.Mime == null || Reader.Item?.Account == null) return;
        Ui.ShowCompose(ComposeViewModel.Reply(AllAccounts, Reader.Item.Account, Reader.Item.Record, Reader.Mime, all));
    }

    [RelayCommand]
    private void Forward()
    {
        if (Reader.Mime == null || Reader.Item?.Account == null) return;
        Ui.ShowCompose(ComposeViewModel.Forward(AllAccounts, Reader.Item.Account, Reader.Mime));
    }

    [RelayCommand]
    private void EditDraft()
    {
        if (Reader.Mime == null || Reader.Item?.Account == null) return;
        var vm = ComposeViewModel.FromDraft(AllAccounts, Reader.Item.Account, Reader.Mime);
        vm.DraftSource = Reader.Item.Record;
        Ui.ShowCompose(vm);
    }

    // ---------------- Accounts ----------------
    [RelayCommand]
    private void AddAccount()
    {
        var account = Ui.ShowAccountEditor(null);
        if (account == null) return;
        var card = new AccountCardViewModel(account);
        Accounts.Add(card);
        _engine.Register(account);
        OnPropertyChanged(nameof(HasAccounts));
        SelectAccount(card);
        _ = Task.Run(() => _engine.SyncFolderAsync(account.Id, FolderKind.Inbox));
    }

    [RelayCommand]
    private void EditAccount(AccountCardViewModel card)
    {
        var updated = Ui.ShowAccountEditor(card.Account);
        if (updated == null) return;
        card.Account = updated;
        _engine.Register(updated);
        ReloadList();
        _ = Task.Run(() => _engine.SyncFolderAsync(updated.Id, FolderKind.Inbox));
    }

    [RelayCommand]
    private void RemoveAccount(AccountCardViewModel card)
    {
        if (!Ui.Confirm($"Remove {card.Email} from QuickMail?\n\nNothing is deleted from the server; only the local copy is removed.", "Remove account"))
            return;
        _engine.Unregister(card.Account.Id);
        MailStore.DeleteAccount(card.Account.Id);
        Accounts.Remove(card);
        if (SelectedAccount == card) SelectedAccount = null;
        OnPropertyChanged(nameof(HasAccounts));
        ReloadList();
        RefreshCounts();
    }

    [RelayCommand]
    private void SyncAccount(AccountCardViewModel card) => _ = Task.Run(() => _engine.SyncFolderAsync(card.Account.Id, FolderKind.Inbox));

    [RelayCommand] private void MoveAccountUp(AccountCardViewModel card) => MoveAccount(card, -1);
    [RelayCommand] private void MoveAccountDown(AccountCardViewModel card) => MoveAccount(card, 1);

    public void MoveAccount(AccountCardViewModel card, int delta)
    {
        int i = Accounts.IndexOf(card), j = i + delta;
        if (i < 0 || j < 0 || j >= Accounts.Count) return;
        Accounts.Move(i, j);
        MailStore.SetSortOrders(Accounts.Select(a => a.Account));
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Ui.ShowSettings(this);
        _trusted = MailStore.GetTrusted();
        ReloadList();
        RefreshCounts();
    }

    /// <summary>Called after a settings import so new accounts appear without restarting.</summary>
    public void ReloadAccounts()
    {
        var accounts = MailStore.GetAccounts();
        foreach (var gone in Accounts.Where(c => accounts.All(a => a.Id != c.Account.Id)).ToList())
        {
            if (SelectedAccount == gone) SelectedAccount = null;
            Accounts.Remove(gone);
        }
        foreach (var a in accounts)
        {
            var card = Accounts.FirstOrDefault(c => c.Account.Id == a.Id);
            if (card == null) Accounts.Add(new AccountCardViewModel(a));
            else card.Account = a;
            _engine.Register(a);
        }
        // keep the sidebar order in line with the shared order
        for (int i = 0; i < accounts.Count; i++)
        {
            var at = Accounts.IndexOf(Accounts.First(c => c.Account.Id == accounts[i].Id));
            if (at != i) Accounts.Move(at, i);
        }
        ReloadTags();
        DarkReading = AppSettings.Current.DarkReading;
        OnPropertyChanged(nameof(HasAccounts));
        ReloadList();
        RefreshCounts();
        _ = Task.Run(() => _engine.SyncAllAsync(FolderKind.Inbox));
    }
}
