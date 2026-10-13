using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using QuickMail.Data;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ComposeWindow : Window
{
    private readonly ComposeViewModel _vm;
    private bool _editorReady;
    private bool _closingConfirmed;
    private readonly Popup _suggestPopup;
    private readonly ListBox _suggestList;
    private TextBox? _suggestTarget;

    public ComposeWindow(ComposeViewModel vm)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _vm = vm;
        DataContext = vm;
        Owner = Application.Current.MainWindow;

        vm.GetBodyHtml = GetHtmlAsync;
        vm.SignatureChanged += html => { if (_editorReady) _ = Editor.ExecuteScriptAsync($"setSignature({JsonSerializer.Serialize(html)})"); };
        vm.CloseRequested += () => { _closingConfirmed = true; Close(); };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ComposeViewModel.To) or nameof(ComposeViewModel.Cc) or nameof(ComposeViewModel.Bcc) or nameof(ComposeViewModel.Subject))
                vm.IsDirty = true;
        };

        (_suggestPopup, _suggestList) = BuildSuggestPopup();
        foreach (var box in new[] { ToBox, CcBox, BccBox })
        {
            box.TextChanged += Recipient_TextChanged;
            box.PreviewKeyDown += Recipient_PreviewKeyDown;
            box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { if (!_suggestList.IsKeyboardFocusWithin) _suggestPopup.IsOpen = false; });
        }

        Loaded += async (_, _) => await InitEditorAsync();
        Closing += OnClosing;
    }

    private async Task InitEditorAsync()
    {
        await Editor.EnsureCoreWebView2Async(await WebViewEnv.GetAsync());
        var core = Editor.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("data:") && !e.Uri.StartsWith("about:")) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += async (_, e) =>
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "dirty": _vm.IsDirty = true; break;
                case "send": if (_vm.SendCommand.CanExecute(null)) await _vm.SendCommand.ExecuteAsync(null); break;
                case "save": await SaveDraftAsync(); break;
            }
        };
        core.NavigationCompleted += async (_, _) =>
        {
            if (_editorReady) return;
            _editorReady = true;
            await Editor.ExecuteScriptAsync($"setContent({JsonSerializer.Serialize(_vm.InitialHtml)})");
            _vm.IsDirty = false;
            if (string.IsNullOrWhiteSpace(_vm.To)) ToBox.Focus();
            else if (string.IsNullOrWhiteSpace(_vm.Subject)) SubjectBox.Focus();
            else
            {
                Editor.Focus();
                await Editor.ExecuteScriptAsync("focusEditor()");
            }
        };
        core.NavigateToString(EditorPage.Html);
    }

    private async Task<string> GetHtmlAsync()
    {
        if (!_editorReady) return _vm.InitialHtml;
        var json = await Editor.ExecuteScriptAsync("getContent()");
        return ComposeViewModel.Decode(json);
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await SaveDraftAsync();

    private async Task SaveDraftAsync()
    {
        if (await _vm.SaveDraftAsync())
        {
            _closingConfirmed = true;
            Close();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed || !_vm.IsDirty || _vm.IsSending) return;
        e.Cancel = true;
        if (Ui.Confirm("Save this message to Drafts before closing?\n\nChoose Cancel to discard it.", "Save draft?"))
        {
            if (!await _vm.SaveDraftAsync()) return;
        }
        _closingConfirmed = true;
        Close();
    }

    // ---------------- Attachments ----------------
    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files" };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) _vm.AttachmentPaths.Add(f);
        _vm.IsDirty = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files.Where(File.Exists)) _vm.AttachmentPaths.Add(f);
        _vm.IsDirty = true;
    }

    // ---------------- Recipient autocomplete ----------------
    private (Popup, ListBox) BuildSuggestPopup()
    {
        var list = new ListBox { MaxHeight = 260, MinWidth = 360, Padding = new Thickness(4) };
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border x:Name="B" Padding="10,6" CornerRadius="6" Background="Transparent" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel>
                  <TextBlock Text="{Binding Name}" Foreground="#E8EAF0"/>
                  <TextBlock Text="{Binding Address}" Foreground="#A0A8BA" FontSize="11.5"/>
                </StackPanel>
              </Border>
              <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
                  <Setter TargetName="B" Property="Background" Value="#262150"/>
                </DataTrigger>
              </DataTemplate.Triggers>
            </DataTemplate>
            """);
        list.PreviewMouseLeftButtonUp += (_, _) => AcceptSuggestion();
        var border = new Border
        {
            Child = list,
            Background = (System.Windows.Media.Brush)FindResource("Bg2"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Bg4"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 0)
        };
        var popup = new Popup { Child = border, AllowsTransparency = true, Placement = PlacementMode.Bottom, StaysOpen = false };
        return (popup, list);
    }

    private static string CurrentToken(TextBox box)
    {
        var text = box.Text[..box.CaretIndex];
        var cut = Math.Max(text.LastIndexOf(','), text.LastIndexOf(';'));
        return text[(cut + 1)..].Trim();
    }

    private void Recipient_TextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var token = CurrentToken(box);
        if (token.Length < 2 || token.Contains('<'))
        {
            _suggestPopup.IsOpen = false;
            return;
        }
        var matches = MailStore.SearchContacts(token, 8);
        if (matches.Count == 0)
        {
            _suggestPopup.IsOpen = false;
            return;
        }
        _suggestTarget = box;
        _suggestList.ItemsSource = matches.Select(m => new { Name = string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name, m.Address, Contact = m }).ToList();
        _suggestList.SelectedIndex = 0;
        _suggestPopup.PlacementTarget = box;
        _suggestPopup.IsOpen = true;
    }

    private void Recipient_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_suggestPopup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                _suggestList.SelectedIndex = Math.Min(_suggestList.Items.Count - 1, _suggestList.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                _suggestList.SelectedIndex = Math.Max(0, _suggestList.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                AcceptSuggestion();
                e.Handled = true;
                break;
            case Key.Escape:
                _suggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void AcceptSuggestion()
    {
        if (_suggestTarget == null || _suggestList.SelectedItem == null) return;
        var contact = (ContactRecord)_suggestList.SelectedItem.GetType().GetProperty("Contact")!.GetValue(_suggestList.SelectedItem)!;
        var box = _suggestTarget;
        var before = box.Text[..box.CaretIndex];
        var after = box.Text[box.CaretIndex..];
        var cut = Math.Max(before.LastIndexOf(','), before.LastIndexOf(';'));
        var prefix = cut >= 0 ? before[..(cut + 1)] + " " : "";
        var rest = after.Contains(',') ? after[after.IndexOf(',')..] : "";
        var insert = contact.Display + ", ";
        box.Text = prefix.TrimStart() + insert + rest.TrimStart(',', ' ');
        box.CaretIndex = (prefix.TrimStart() + insert).Length;
        _suggestPopup.IsOpen = false;
        box.Focus();
    }
}

public class FileNameConverter : IValueConverter
{
    public static readonly FileNameConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Path.GetFileName(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
