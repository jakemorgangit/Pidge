using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Data;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>Type-to-filter list of an account's folders, used by "Move to folder".</summary>
public class FolderPickerWindow : Window
{
    private record Entry(FolderKind Kind, string Path, string Label, int Depth)
    {
        public Thickness Indent => new(Depth * 16, 0, 0, 0);
    }

    private readonly List<Entry> _all;
    private readonly ListBox _list;
    private readonly TextBox _filter;

    public (FolderKind kind, string path)? Result { get; private set; }

    public FolderPickerWindow(long accountId)
    {
        Title = "Move to folder";
        Width = 420;
        Height = 520;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Style = (Style)Application.Current.Resources["DarkWindow"];
        WindowTheme.ApplyDark(this);

        _all =
        [
            new(FolderKind.Inbox, "", "Inbox", 0),
            new(FolderKind.Archive, "", "Archive", 0),
            new(FolderKind.Junk, "", "Spam", 0),
            new(FolderKind.Trash, "", "Bin", 0),
            .. MailStore.GetFolders(accountId).Where(f => f.Selectable).Select(f => new Entry(FolderKind.Custom, f.Path, f.Name, f.Depth + 1))
        ];

        var root = new DockPanel { Margin = new Thickness(20) };
        _filter = new TextBox { Tag = "Type to filter folders", Margin = new Thickness(0, 0, 0, 10) };
        _filter.TextChanged += (_, _) => Refresh();
        _filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { _list.Focus(); _list.SelectedIndex = Math.Max(0, _list.SelectedIndex); e.Handled = true; }
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        DockPanel.SetDock(_filter, Dock.Top);
        root.Children.Add(_filter);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        var ok = new Button { Content = "Move", Style = (Style)FindResource("AccentButton"), IsDefault = true, MinWidth = 90 };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _list = new ListBox { ItemTemplate = BuildTemplate() };
        _list.MouseDoubleClick += (_, _) => Accept();
        root.Children.Add(_list);
        Content = root;

        Refresh();
        Loaded += (_, _) => _filter.Focus();
    }

    private void Refresh()
    {
        var q = _filter.Text.Trim();
        _list.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(e => e.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(e => e with { Depth = 0 }).ToList();
        _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Entry e) return;
        Result = (e.Kind, e.Path);
        DialogResult = true;
    }

    private static DataTemplate BuildTemplate() => (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="B" Padding="10,7" CornerRadius="6" Background="Transparent">
            <StackPanel Orientation="Horizontal" Margin="{Binding Indent}">
              <TextBlock Text="&#xE8B7;" FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" Foreground="#A0A8BA" VerticalAlignment="Center"/>
              <TextBlock Text="{Binding Label}" Foreground="#E8EAF0" Margin="10,0,0,0"/>
            </StackPanel>
          </Border>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
              <Setter TargetName="B" Property="Background" Value="#262150"/>
            </DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);
}
