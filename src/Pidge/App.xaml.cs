using System.Windows;
using System.Windows.Threading;
using Pidge.Data;
using Pidge.Services;
using Pidge.Views;

namespace Pidge;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private bool _started;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // One Pidge per user: a second launch just brings the first window forward.
        _singleInstance = new Mutex(true, @"Local\Pidge.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var other = System.Diagnostics.Process.GetProcessesByName(current.ProcessName).FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);
            if (other != null)
            {
                ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
            }
            Shutdown();
            return;
        }

        _started = true;
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        Db.Initialize();
        AppSettings.Load();
        SpamEngine.Load();
        foreach (var account in MailStore.GetAccounts()) MailEngine.Instance.Register(account);

        Notifier.Initialize();
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        MailEngine.Instance.StartBackgroundSync();
        ConfigSync.Start();
        _ = ConfigSync.PullAsync();

        // Daily tidy-up: compress older cache files and apply the retention setting.
        if (DateTime.UtcNow - AppSettings.Current.LastCompactUtc > TimeSpan.FromDays(1))
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(2));
                try { await MessageCache.CompactAsync(AppSettings.Current.CacheRetentionDays); } catch { /* try again tomorrow */ }
            });
        if (AppSettings.Current.CheckForUpdates && !MailEngine.Offline) _ = UpdateService.CheckOnStartupAsync(main);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_started) { base.OnExit(e); return; }
        MailEngine.Instance.Stop();
        SpamEngine.Flush();
        AppSettings.Save();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Pidge", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
