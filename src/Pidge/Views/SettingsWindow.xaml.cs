using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pidge.Data;
using Pidge.Helpers;
using Pidge.Models;
using Pidge.Services;
using Pidge.ViewModels;

namespace Pidge.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;
    private readonly double _originalScale;
    private bool _saved;

    private record Choice(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    private record Suggestion(string Label, string Path);

    private class NotifyRow
    {
        public required MailAccount Account { get; init; }
        public bool Enabled { get; set; }
        public string Name => string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.Email : Account.DisplayName;
        public string Email => Account.Email;
        public System.Windows.Media.SolidColorBrush Brush => Palette.FromHex(Account.Color);
    }

    private class TagRow
    {
        public required TagInfo Tag { get; init; }
        public required List<TagRule> Rules { get; init; }
        public string Name => Tag.Name;
        public System.Windows.Media.SolidColorBrush Brush => Palette.FromHex(Tag.Color);
        public string CountText => $"{Tag.Count:N0} message{(Tag.Count == 1 ? "" : "s")}";
    }

    private static readonly Choice[] Intervals = [new("1 minute", 1), new("2 minutes", 2), new("5 minutes", 5), new("15 minutes", 15), new("30 minutes", 30)];
    private static readonly Choice[] Counts = [new("100 messages", 100), new("300 messages", 300), new("1,000 messages", 1000), new("3,000 messages", 3000)];
    private static readonly Choice[] Retention =
        [new("Never (keep everything)", 0), new("1 year", 365), new("6 months", 182), new("90 days", 90), new("30 days", 30)];

    public SettingsWindow(MainViewModel main, string? section = null)
    {
        InitializeComponent();
        if (section != null)
            Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header as string)?.StartsWith(section, StringComparison.OrdinalIgnoreCase) == true) ?? Tabs.Items[0];
        WindowTheme.ApplyDark(this);
        _main = main;
        _originalScale = main.UiScale;

        var s = AppSettings.Current;
        ScaleSlider.Value = main.UiScale;
        ScaleText.Text = $"{main.UiScale:P0}";
        IntervalBox.ItemsSource = Intervals;
        IntervalBox.SelectedItem = Intervals.FirstOrDefault(i => i.Value == s.SyncIntervalMinutes) ?? Intervals[1];
        InitialCountBox.ItemsSource = Counts;
        InitialCountBox.SelectedItem = Counts.FirstOrDefault(i => i.Value == s.InitialSyncCount) ?? Counts[1];
        BlockImagesBox.IsChecked = s.BlockRemoteImages;
        DarkReadingBox.IsChecked = s.DarkReading;
        BrandLogosBox.IsChecked = s.ShowBrandLogos;
        BrandTintBox.IsChecked = s.BrandTint;
        UpdatesBox.IsChecked = s.CheckForUpdates;
        ThresholdSlider.Value = s.SpamThreshold;
        MoveSpamBox.IsChecked = s.MoveSpamToJunk;
        ClientIdBox.Text = s.MicrosoftClientId;
        NotifyEnabledBox.IsChecked = s.NotificationsEnabled;
        NotifySoundBox.IsChecked = s.NotifySound;
        NotifyPeopleBox.IsChecked = s.NotifyPeopleOnly;
        NotifyAccountsList.ItemsSource = MailStore.GetAccounts().Select(a => new NotifyRow { Account = a, Enabled = !Notifier.IsAccountMuted(a) }).ToList();
        RetentionBox.ItemsSource = Retention;
        RetentionBox.SelectedItem = Retention.FirstOrDefault(r => r.Value == s.CacheRetentionDays) ?? Retention[0];

        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        RepoText.Text = "github.com/" + UpdateService.Repository;
        DataPathText.Text = Db.DataDir;

        RefreshLearning();
        RefreshBlocked();
        RefreshTemplates();
        RefreshTags();
        RefreshStorage();
        RefreshSync();
        ConfigSync.StatusChanged += OnSyncStatus;

        Closed += (_, _) =>
        {
            ConfigSync.StatusChanged -= OnSyncStatus;
            if (!_saved) _main.UiScale = _originalScale;
        };
    }

    // ---------------- General ----------------
    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleText == null) return;
        var v = Math.Round(e.NewValue, 2);
        ScaleText.Text = $"{v:P0}";
        _main.UiScale = v; // live preview
    }

    // ---------------- Notifications ----------------
    private void TestNotification_Click(object sender, RoutedEventArgs e) => Notifier.ShowTest();

    // ---------------- Accounts & servers ----------------
    private void RefreshTemplates() => TemplateList.ItemsSource = MailStore.GetTemplates();

    private void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TplName.Text) || string.IsNullOrWhiteSpace(TplImap.Text) || string.IsNullOrWhiteSpace(TplSmtp.Text)) return;
        MailStore.SaveTemplate(new ServerTemplate
        {
            Name = TplName.Text.Trim(), ImapHost = TplImap.Text.Trim(), ImapPort = 993, ImapSecurity = 2,
            SmtpHost = TplSmtp.Text.Trim(), SmtpPort = 465, SmtpSecurity = 2
        });
        TplName.Text = TplImap.Text = TplSmtp.Text = "";
        RefreshTemplates();
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ServerTemplate t) return;
        MailStore.DeleteTemplate(t.Id);
        RefreshTemplates();
    }

    private void RegistrationHelp_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Repository}#microsoft-sign-in-setup") { UseShellExecute = true });

    // ---------------- Tags ----------------
    private void RefreshTags()
    {
        var rules = MailStore.GetTagRules();
        var rows = MailStore.GetTags().Select(t => new TagRow { Tag = t, Rules = rules.Where(r => r.TagId == t.Id).ToList() }).ToList();
        TagList.ItemsSource = rows;
        NoTagsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not TagRule r) return;
        MailStore.RemoveTagRule(r.TagId, r.Pattern);
        RefreshTags();
    }

    private void RenameTag_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not TagRow row) return;
        var name = Ui.AskText("Rename tag", "New name for this tag:", row.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        row.Tag.Name = name.Trim();
        MailStore.UpdateTag(row.Tag);
        RefreshTags();
    }

    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not TagRow row) return;
        if (!Ui.Confirm($"Delete the tag \"{row.Name}\"? Messages aren't deleted, they just lose the tag.", "Delete tag")) return;
        MailStore.DeleteTag(row.Tag.Id);
        RefreshTags();
    }

    // ---------------- Spam / blocked ----------------
    private void RefreshLearning() =>
        LearnText.Text = $"{SpamEngine.SpamDocs:N0} messages marked as spam · {SpamEngine.HamDocs:N0} marked as not spam · " +
                         $"{MailStore.GetCounter("spamCaught"):N0} spam caught so far";

    private void RefreshBlocked() =>
        BlockedList.ItemsSource = MailStore.GetBlocked().Select(b => new
        {
            b.Pattern,
            Detail = $"Blocked {DateTimeOffset.FromUnixTimeSeconds(b.CreatedUnix).LocalDateTime:d MMM yyyy} · {b.Hits} message{(b.Hits == 1 ? "" : "s")} binned since"
        }).ToList();

    private async void AddBlock_Click(object sender, RoutedEventArgs e)
    {
        var p = BlockBox.Text.Trim().ToLowerInvariant();
        if (!p.Contains('@') || p.Length < 3) return;
        BlockBox.Text = "";
        await Task.Run(() => MailEngine.Instance.BlockAsync(p));
        RefreshBlocked();
    }

    private void Unblock_Click(object sender, RoutedEventArgs e)
    {
        MailStore.RemoveBlocked((string)((Button)sender).Tag);
        SpamEngine.RefreshLists();
        RefreshBlocked();
    }

    private void ResetLearning_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Forget everything the spam filter has learned from you?", "Reset learning")) return;
        SpamEngine.Reset();
        RefreshLearning();
    }

    // ---------------- Storage ----------------
    private void RefreshStorage()
    {
        var r = MessageCache.Measure();
        StorageTotal.Text = MessageCache.FormatBytes(r.TotalBytes);
        StorageDb.Text = MessageCache.FormatBytes(r.DatabaseBytes);
        StorageCache.Text = $"{MessageCache.FormatBytes(r.CacheBytes)} · {r.CachedMessages:N0}";
    }

    private async void Compact_Click(object sender, RoutedEventArgs e)
    {
        CompactButton.IsEnabled = false;
        CompactResult.Text = "Compressing and tidying…";
        try
        {
            var days = ((Choice)RetentionBox.SelectedItem).Value;
            var (before, after) = await Task.Run(() => MessageCache.CompactAsync(days));
            CompactResult.Text = before > after
                ? $"Saved {MessageCache.FormatBytes(before - after)} ({MessageCache.FormatBytes(before)} → {MessageCache.FormatBytes(after)})."
                : "Already tidy.";
            RefreshStorage();
        }
        catch (Exception ex)
        {
            CompactResult.Text = "Couldn't finish: " + ex.Message;
        }
        finally
        {
            CompactButton.IsEnabled = true;
        }
    }

    // ---------------- Sync between PCs ----------------
    private void RefreshSync()
    {
        bool on = ConfigSync.IsEnabled;
        SyncOnPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncOffPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SyncFolderText.Text = AppSettings.Current.SharedFolder;
        SyncStatusText.Text = ConfigSync.Status;
        SyncError.Text = "";
        if (!on) SyncSuggestions.ItemsSource = FindCloudFolders();
    }

    private void OnSyncStatus(string status) => Dispatcher.BeginInvoke(() => SyncStatusText.Text = status);

    /// <summary>Spots Google Drive, OneDrive and Dropbox folders on this PC.</summary>
    private static List<Suggestion> FindCloudFolders()
    {
        var found = new List<Suggestion>();
        void Add(string label, string? root)
        {
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root) && found.All(f => !f.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                found.Add(new Suggestion(label, Path.Combine(root, "Pidge")));
        }
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            Add($"Google Drive ({d.Name.TrimEnd('\\')})", Path.Combine(d.RootDirectory.FullName, "My Drive"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Google Drive", Path.Combine(home, "Google Drive"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive (work)", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("Dropbox", Path.Combine(home, "Dropbox"));
        return found;
    }

    private void SyncSuggestion_Click(object sender, RoutedEventArgs e) => SyncFolderBox.Text = (string)((Button)sender).Tag;

    private void BrowseSync_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the shared folder" };
        if (dlg.ShowDialog(this) == true) SyncFolderBox.Text = dlg.FolderName;
    }

    private async void SyncOn_Click(object sender, RoutedEventArgs e)
    {
        SyncError.Text = "";
        var folder = SyncFolderBox.Text.Trim();
        if (folder.Length == 0) { SyncError.Text = "Choose a folder first."; return; }
        if (SyncPass1.Password.Length < 8) { SyncError.Text = "Use a passphrase of at least 8 characters."; return; }
        if (SyncPass1.Password != SyncPass2.Password) { SyncError.Text = "The passphrases don't match."; return; }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var message = await ConfigSync.EnableAsync(folder, SyncPass1.Password);
            Mouse.OverrideCursor = null;
            RefreshSync();
            RefreshTemplates();
            RefreshTags();
            Ui.Info(message, "Sync between PCs");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            SyncError.Text = ex is InvalidDataException && ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "That folder already has shared settings with a different passphrase. Use the passphrase from your other PC."
                : ex.Message;
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await ConfigSync.PullAsync();
        await ConfigSync.PushAsync();
        SyncStatusText.Text = ConfigSync.Status;
    }

    private void SyncOff_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Stop sharing settings on this PC? Your accounts stay here; changes just won't sync any more. The shared file is left in place for your other PCs.", "Turn off sync"))
            return;
        ConfigSync.Disable();
        RefreshSync();
    }

    // ---------------- Backup ----------------
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Pidge-{DateTime.Now:yyyy-MM-dd}.pidgebackup",
            Filter = "Pidge backup (*.pidgebackup)|*.pidgebackup",
            Title = "Export Pidge settings"
        };
        if (dlg.ShowDialog(this) != true) return;
        var password = Ui.AskPassword("Protect your backup",
            "Choose a password for this file. You'll need it to import on another computer. It contains your mail passwords, so pick something strong.", confirm: true);
        if (password == null) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            SettingsTransfer.Export(dlg.FileName, password);
            Mouse.OverrideCursor = null;
            Ui.Info($"Saved to {dlg.FileName}.\n\nKeep it somewhere safe. Anyone with the file and the password can access your accounts.", "Export complete");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Ui.Info(ex.Message, "Export failed");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pidge backup (*.pidgebackup;*.qmbackup)|*.pidgebackup;*.qmbackup|All files|*.*", Title = "Import Pidge settings" };
        if (dlg.ShowDialog(this) != true) return;
        var password = Ui.AskPassword("Unlock backup", "Enter the password this backup was exported with.", confirm: false);
        if (password == null) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var result = SettingsTransfer.Import(dlg.FileName, password);
            Mouse.OverrideCursor = null;
            _main.ReloadAccounts();
            Ui.Info($"Imported {result.AccountsAdded} new account{(result.AccountsAdded == 1 ? "" : "s")}" +
                    (result.AccountsUpdated > 0 ? $", updated {result.AccountsUpdated}" : "") +
                    $" and {result.BlockedAdded} blocked sender{(result.BlockedAdded == 1 ? "" : "s")}. Your inboxes are syncing now.", "Import complete");
            RefreshBlocked();
            RefreshLearning();
            RefreshTemplates();
            RefreshTags();
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Ui.Info(ex.Message, "Import failed");
        }
    }

    // ---------------- About / updates ----------------
    private void OpenRepo_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Repository}") { UseShellExecute = true });

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        try
        {
            var latest = await UpdateService.GetLatestAsync();
            if (latest == null) UpdateStatus.Text = "Couldn't find any releases.";
            else if (latest.Version <= UpdateService.CurrentVersion) UpdateStatus.Text = "You're on the latest version.";
            else
            {
                UpdateStatus.Text = $"Version {latest.Version} is available.";
                new UpdateWindow(latest) { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Couldn't check: " + ex.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    // ---------------- Save ----------------
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        bool intervalChanged = s.SyncIntervalMinutes != ((Choice)IntervalBox.SelectedItem).Value;
        s.SyncIntervalMinutes = ((Choice)IntervalBox.SelectedItem).Value;
        s.InitialSyncCount = ((Choice)InitialCountBox.SelectedItem).Value;
        s.BlockRemoteImages = BlockImagesBox.IsChecked == true;
        s.CheckForUpdates = UpdatesBox.IsChecked == true;
        s.ShowBrandLogos = BrandLogosBox.IsChecked == true;
        s.BrandTint = BrandTintBox.IsChecked == true;
        s.SpamThreshold = Math.Round(ThresholdSlider.Value, 2);
        s.MoveSpamToJunk = MoveSpamBox.IsChecked == true;
        s.MicrosoftClientId = ClientIdBox.Text.Trim();
        s.CacheRetentionDays = ((Choice)RetentionBox.SelectedItem).Value;
        s.NotificationsEnabled = NotifyEnabledBox.IsChecked == true;
        s.NotifySound = NotifySoundBox.IsChecked == true;
        s.NotifyPeopleOnly = NotifyPeopleBox.IsChecked == true;
        foreach (var row in (IEnumerable<NotifyRow>)NotifyAccountsList.ItemsSource) Notifier.SetAccountMuted(row.Account, !row.Enabled);
        _main.DarkReading = DarkReadingBox.IsChecked == true;
        _saved = true;
        AppSettings.Save();
        MailStore.NotifyConfigChanged();
        if (intervalChanged) MailEngine.Instance.StartBackgroundSync();
        DialogResult = true;
    }
}
