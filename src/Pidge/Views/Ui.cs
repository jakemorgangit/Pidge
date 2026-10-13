using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pidge.Helpers;
using Pidge.Models;
using Pidge.Views;

namespace Pidge.ViewModels;

/// <summary>Small façade so view models can open windows and ask questions.</summary>
public static class Ui
{
    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public static void ShowCompose(ComposeViewModel vm) => new ComposeWindow(vm).Show();

    public static MailAccount? ShowAccountEditor(MailAccount? existing)
    {
        var w = new AccountWindow(existing) { Owner = Owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    public static void ShowSettings(MainViewModel main, string? section = null) => new SettingsWindow(main, section) { Owner = Owner }.ShowDialog();

    public static bool Confirm(string message, string title) => Dialog(title, message, "OK", "Cancel", null, false).ok;

    /// <summary>Asks for a line of text; returns null if cancelled.</summary>
    public static string? AskText(string title, string message, string initial)
    {
        var (ok, value) = Dialog(title, message, "OK", "Cancel", "text:" + initial, false);
        return ok ? value : null;
    }

    /// <summary>Lets the user choose a destination folder in one account.</summary>
    public static (Pidge.Models.FolderKind kind, string path)? PickFolder(long accountId)
    {
        var w = new FolderPickerWindow(accountId) { Owner = Owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    public static void Info(string message, string title) => Dialog(title, message, "OK", null, null, false);

    /// <summary>Asks for a password. When <paramref name="confirm"/> is true the user must type it twice.</summary>
    public static string? AskPassword(string title, string message, bool confirm)
    {
        var (ok, value) = Dialog(title, message, "Continue", "Cancel", "password", confirm);
        return ok ? value : null;
    }

    private static (bool ok, string? value) Dialog(string title, string message, string okText, string? cancelText, string? input, bool confirmInput)
    {
        var window = new Window
        {
            Title = title,
            Owner = Owner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Style = (Style)Application.Current.Resources["DarkWindow"]
        };
        WindowTheme.ApplyDark(window);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SubBrush"], LineHeight = 20
        });

        PasswordBox? pw1 = null, pw2 = null;
        TextBox? text = null;
        var error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DangerBrush"], Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        if (input != null && input.StartsWith("text:"))
        {
            text = new TextBox { Margin = new Thickness(0, 16, 0, 0), Text = input[5..] };
            text.SelectAll();
            root.Children.Add(text);
        }
        else if (input != null)
        {
            pw1 = new PasswordBox { Margin = new Thickness(0, 16, 0, 0) };
            root.Children.Add(pw1);
            if (confirmInput)
            {
                root.Children.Add(new TextBlock { Text = "Confirm password", Style = (Style)Application.Current.Resources["FieldLabel"] });
                pw2 = new PasswordBox();
                root.Children.Add(pw2);
            }
            root.Children.Add(error);
        }

        bool ok = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, Style = (Style)Application.Current.Resources["OutlineButton"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
            cancel.Click += (_, _) => window.Close();
            buttons.Children.Add(cancel);
        }
        var okButton = new Button { Content = okText, Style = (Style)Application.Current.Resources["AccentButton"], IsDefault = true, MinWidth = 90 };
        okButton.Click += (_, _) =>
        {
            if (pw1 != null)
            {
                if (confirmInput && pw1.Password.Length < 8) { error.Text = "Use at least 8 characters."; error.Visibility = Visibility.Visible; return; }
                if (pw2 != null && pw1.Password != pw2.Password) { error.Text = "The passwords don't match."; error.Visibility = Visibility.Visible; return; }
                if (pw1.Password.Length == 0) { error.Text = "Enter the password."; error.Visibility = Visibility.Visible; return; }
            }
            ok = true;
            window.Close();
        };
        buttons.Children.Add(okButton);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, _) =>
        {
            if (text != null) { Keyboard.Focus(text); text.SelectAll(); }
            else if (pw1 != null) Keyboard.Focus(pw1);
            else Keyboard.Focus(okButton);
        };
        window.ShowDialog();
        return (ok, text?.Text ?? pw1?.Password);
    }
}
