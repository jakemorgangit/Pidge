using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Pidge.Helpers;
using Pidge.Services;
using Pidge.ViewModels;

namespace Pidge.Views;

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
                Title = _vm.TotalUnread > 0 ? $"Pidge ({_vm.TotalUnread})" : "Pidge";
        };
        _vm.NewMailArrived += (accountId, fresh) =>
        {
            if (IsActive && WindowState != WindowState.Minimized)
            {
                Log.Info($"New mail ({fresh.Count}) while Pidge is in front: no notification");
                return; // you're already looking at Pidge
            }
            FlashTaskbar();
            if (fresh.Any(m => !m.IsSpam && !m.IsRead)) ShowNewMailBadge(true);
            if (_vm.Accounts.FirstOrDefault(a => a.Account.Id == accountId)?.Account is { } account)
                Notifier.ShowNewMail(account, fresh);
        };
        _vm.ActivateRequested += () =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.UiScale) && _webReady && Math.Abs(ReaderView.ZoomFactor - _vm.UiScale) > 0.01)
                ReaderView.ZoomFactor = _vm.UiScale;
            if (e.PropertyName == nameof(MainViewModel.IsCustomFolder)) FoldersTab.IsChecked = _vm.IsCustomFolder;
        };

        Loaded += async (_, _) => await InitWebViewAsync();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Delta > 0) _vm.ZoomInCommand.Execute(null); else _vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        };
        Closing += OnClosing;
        Activated += (_, _) => ShowNewMailBadge(false); // you've seen it
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
            ReaderView.ZoomFactor = _vm.UiScale;
            // Ctrl + wheel over the email itself zooms the whole app the same way.
            ReaderView.ZoomFactorChanged += (_, _) =>
            {
                var z = Math.Round(Math.Clamp(ReaderView.ZoomFactor, MainViewModel.MinScale, MainViewModel.MaxScale), 2);
                if (Math.Abs(z - _vm.UiScale) > 0.01) _vm.UiScale = z;
            };
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
            var path = Path.Combine(Path.GetTempPath(), "Pidge", "view.html");
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
        else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.None && MessageList.IsKeyboardFocusWithin)
        {
            ShowTagMenu(TagButton);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused)
        {
            _vm.SearchText = "";
            MessageList.Focus();
            e.Handled = true;
        }
    }

    // ---------------- Notifications ----------------
    private void BellButton_Click(object sender, RoutedEventArgs e)
    {
        BellButton.ContextMenu.DataContext = _vm;
        BellButton.ContextMenu.PlacementTarget = BellButton;
        BellButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        BellButton.ContextMenu.IsOpen = true;
    }

    // ---------------- Folders drop-down ----------------
    private void FoldersTab_Click(object sender, RoutedEventArgs e)
    {
        FoldersTab.IsChecked = _vm.IsCustomFolder;
        _vm.LoadFolderChoices();
        FoldersPopup.IsOpen = true;
        FolderList.Focus();
    }

    private void FolderList_Click(object sender, MouseButtonEventArgs e) => OpenSelectedFolder();

    private void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelectedFolder();
        if (e.Key == Key.Escape) FoldersPopup.IsOpen = false;
    }

    private void OpenSelectedFolder()
    {
        if (FolderList.SelectedItem is not FolderChoice { Selectable: true } choice) return;
        FoldersPopup.IsOpen = false;
        _vm.OpenFolderCommand.Execute(choice);
    }

    // ---------------- Tag menu ----------------
    private void TagButton_Click(object sender, RoutedEventArgs e) => ShowTagMenu(TagButton);
    private void TagMenu_Click(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(() => ShowTagMenu(null));

    /// <summary>Checkable list of tags for the selected messages, plus "New tag…".</summary>
    private void ShowTagMenu(UIElement? anchor)
    {
        var selected = _vm.SelectedMessages.Count > 0 ? _vm.SelectedMessages : _vm.SelectedMessage is { } m ? [m] : [];
        if (selected.Count == 0) return;
        var menu = new ContextMenu();
        foreach (var tag in _vm.Tags)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = tag.Brush, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = tag.Name, Margin = new Thickness(9, 0, 0, 0) });
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = selected.All(s => s.TagIds.Contains(tag.Tag.Id)),
                Command = _vm.ToggleTagCommand,
                CommandParameter = tag
            };
            menu.Items.Add(item);
        }
        if (_vm.Tags.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "New tag…", Icon = "", Command = _vm.NewTagCommand });
        menu.Items.Add(new MenuItem { Header = "Manage tags…", Icon = "", Command = _vm.ManageTagsCommand });
        if (anchor != null && anchor.IsVisible)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        else
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Mail opened in the Unread view is only marked read on the way out: finish that (briefly) before closing.
        if (_vm.HasHeldReads)
        {
            e.Cancel = true;
            await Task.WhenAny(_vm.CommitHeldReadsAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
            Close();
            return;
        }

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

    private static System.Windows.Media.ImageSource? _newMailBadge;

    /// <summary>Little envelope over the taskbar icon while there's new mail you haven't looked at yet.</summary>
    private void ShowNewMailBadge(bool show)
    {
        if (!show && TaskbarItemInfo?.Overlay == null) return;
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.Overlay = show ? _newMailBadge ??= DrawNewMailBadge() : null;
    }

    private static System.Windows.Media.ImageSource DrawNewMailBadge()
    {
        const int size = 32;
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var accent = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush");
            dc.DrawEllipse(accent, null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            var glyph = new System.Windows.Media.FormattedText("", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 18, System.Windows.Media.Brushes.White, 1.0);
            dc.DrawText(glyph, new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
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
