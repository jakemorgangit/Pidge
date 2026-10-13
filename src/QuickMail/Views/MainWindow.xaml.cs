using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using QuickMail.Helpers;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _webReady;
    private string? _pendingHtml;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _vm = new MainViewModel();
        DataContext = _vm;

        var s = AppSettings.Current;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        _vm.RenderHtml += Render;
        _vm.FocusMessage += item =>
        {
            MessageList.ScrollIntoView(item);
            Dispatcher.BeginInvoke(() =>
            {
                if (MessageList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem li) li.Focus();
            });
        };
        _vm.Reader.PropertyChanged += Reader_PropertyChanged;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.TotalUnread))
                Title = _vm.TotalUnread > 0 ? $"QuickMail ({_vm.TotalUnread})" : "QuickMail";
        };
        _vm.NewMailArrived += (_, count, newest) =>
        {
            if (!IsActive) FlashTaskbar();
        };

        Loaded += async (_, _) => await InitWebViewAsync();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await ReaderView.EnsureCoreWebView2Async(await WebViewEnv.GetAsync());
            var core = ReaderView.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            _webReady = true;
            if (_pendingHtml != null) Render(_pendingHtml);
        }
        catch (Exception ex)
        {
            _vm.Status = "Couldn't start the email viewer (WebView2): " + ex.Message;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Our own content is loaded via NavigateToString (about:blank / data:) or a temp file. Everything else opens outside.
        if (e.Uri.StartsWith("data:") || e.Uri.StartsWith("about:") || e.Uri.StartsWith("file:")) return;
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private void OpenExternal(string uri)
    {
        if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var mail = new Uri(uri);
            var subject = System.Text.RegularExpressions.Regex.Match(mail.Query, "subject=([^&]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Ui.ShowCompose(ComposeViewModel.New(_vm.Accounts.Select(a => a.Account), _vm.Reader.Item?.Account,
                Uri.UnescapeDataString(mail.AbsolutePath), subject.Success ? Uri.UnescapeDataString(subject.Groups[1].Value) : ""));
            return;
        }
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        if (_vm.Reader.Item?.IsSpam == true || _vm.Reader.PhishingWarning != null)
        {
            if (!Ui.Confirm($"This message looks suspicious. Open this link anyway?\n\n{uri}", "Open link")) return;
        }
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void Render(string html)
    {
        if (!_webReady) { _pendingHtml = html; return; }
        _pendingHtml = null;
        if (string.IsNullOrEmpty(html))
        {
            ReaderView.CoreWebView2.NavigateToString("<html><body style='background:#fff'></body></html>");
            return;
        }
        // NavigateToString is limited to ~2 MB; larger messages go through a temp file.
        if (html.Length > 1_500_000)
        {
            var path = Path.Combine(Path.GetTempPath(), "QuickMail", "view.html");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, html);
            ReaderView.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
        }
        else
        {
            ReaderView.CoreWebView2.NavigateToString(html);
        }
    }

    private void Reader_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WebView2 always draws above WPF content, so hide it while showing status text underneath.
        if (e.PropertyName is nameof(ReaderViewModel.IsLoading) or nameof(ReaderViewModel.Error))
            ReaderView.Visibility = _vm.Reader.IsLoading || _vm.Reader.Error != null ? Visibility.Hidden : Visibility.Visible;
    }

    private void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _vm.SelectedMessages = MessageList.SelectedItems.Cast<MessageItemViewModel>().ToList();
    }

    private void MessageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool atBottom = e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 4;
        LoadMoreButton.Visibility = atBottom && _vm.CanLoadMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreButton.ContextMenu.DataContext = _vm;
        MoreButton.ContextMenu.IsOpen = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.F)
        {
            _vm.IsDashboard = false;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            _vm.SelectAccountByIndex(e.Key - Key.D1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused)
        {
            _vm.SearchText = "";
            MessageList.Focus();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = AppSettings.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        AppSettings.Save();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private void FlashTaskbar()
    {
        var info = new FlashInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashInfo>(),
            Hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle,
            Flags = 0x2 | 0xC, // tray, until foreground
            Count = 3
        };
        FlashWindowEx(ref info);
    }
}
