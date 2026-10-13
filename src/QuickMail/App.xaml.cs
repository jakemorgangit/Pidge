using System.Windows;
using System.Windows.Threading;
using QuickMail.Data;
using QuickMail.Services;
using QuickMail.Views;

namespace QuickMail;

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
        // One QuickMail per user: a second launch just brings the first window forward.
        _singleInstance = new Mutex(true, @"Local\QuickMail.SingleInstance", out var isFirst);
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

        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        MailEngine.Instance.StartBackgroundSync();
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
        MessageBox.Show(e.Exception.Message, "QuickMail", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
