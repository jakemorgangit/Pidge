using System.Windows;
using Pidge.Helpers;
using Pidge.Services;

namespace Pidge.Views;

public partial class UpdateWindow : Window
{
    private readonly ReleaseInfo _release;
    private CancellationTokenSource? _cts;

    public UpdateWindow(ReleaseInfo release)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _release = release;
        TitleText.Text = $"Pidge {release.Version} is available";
        SubText.Text = $"You have {UpdateService.CurrentVersion} · {release.MsiSize / 1024.0 / 1024.0:0.0} MB download";
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? "Bug fixes and improvements." : release.Notes.Trim();
        Closing += (_, _) => _cts?.Cancel();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SkippedVersion = _release.Tag;
        AppSettings.Save();
        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = SkipButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading…";
        _cts = new CancellationTokenSource();
        try
        {
            var path = await UpdateService.DownloadAsync(_release, new Progress<double>(p => Progress.Value = p), _cts.Token);
            StatusText.Text = "Installing. Pidge will reopen when it's done.";
            await Task.Delay(600);
            UpdateService.InstallAndRestart(path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = "The update couldn't be downloaded: " + ex.Message;
            InstallButton.IsEnabled = SkipButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
