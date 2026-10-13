using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Data;
using QuickMail.Helpers;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;

    private record Choice(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    private static readonly Choice[] Intervals = [new("1 minute", 1), new("2 minutes", 2), new("5 minutes", 5), new("15 minutes", 15), new("30 minutes", 30)];
    private static readonly Choice[] Counts = [new("100 messages", 100), new("300 messages", 300), new("1,000 messages", 1000), new("3,000 messages", 3000)];

    public SettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _main = main;

        var s = AppSettings.Current;
        IntervalBox.ItemsSource = Intervals;
        IntervalBox.SelectedItem = Intervals.FirstOrDefault(i => i.Value == s.SyncIntervalMinutes) ?? Intervals[1];
        InitialCountBox.ItemsSource = Counts;
        InitialCountBox.SelectedItem = Counts.FirstOrDefault(i => i.Value == s.InitialSyncCount) ?? Counts[1];
        BlockImagesBox.IsChecked = s.BlockRemoteImages;
        DarkReadingBox.IsChecked = s.DarkReading;
        UpdatesBox.IsChecked = s.CheckForUpdates;
        ThresholdSlider.Value = s.SpamThreshold;
        MoveSpamBox.IsChecked = s.MoveSpamToJunk;
        VersionText.Text = $"QuickMail {UpdateService.CurrentVersion}";
        DataPathRun.Text = Db.DataDir;

        RefreshLearning();
        RefreshBlocked();
    }

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

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"QuickMail-{DateTime.Now:yyyy-MM-dd}.qmbackup",
            Filter = "QuickMail backup (*.qmbackup)|*.qmbackup",
            Title = "Export QuickMail settings"
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "QuickMail backup (*.qmbackup)|*.qmbackup|All files|*.*", Title = "Import QuickMail settings" };
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
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Ui.Info(ex.Message, "Import failed");
        }
    }

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        bool intervalChanged = s.SyncIntervalMinutes != ((Choice)IntervalBox.SelectedItem).Value;
        s.SyncIntervalMinutes = ((Choice)IntervalBox.SelectedItem).Value;
        s.InitialSyncCount = ((Choice)InitialCountBox.SelectedItem).Value;
        s.BlockRemoteImages = BlockImagesBox.IsChecked == true;
        s.CheckForUpdates = UpdatesBox.IsChecked == true;
        s.SpamThreshold = Math.Round(ThresholdSlider.Value, 2);
        s.MoveSpamToJunk = MoveSpamBox.IsChecked == true;
        _main.DarkReading = DarkReadingBox.IsChecked == true;
        AppSettings.Save();
        if (intervalChanged) MailEngine.Instance.StartBackgroundSync();
        DialogResult = true;
    }
}
