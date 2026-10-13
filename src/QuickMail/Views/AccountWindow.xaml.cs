using System.Windows;
using System.Windows.Controls;
using QuickMail.Data;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class AccountWindow : Window
{
    private record SecurityOption(string Label, int Value)
    {
        public override string ToString() => Label;
    }

    private static readonly SecurityOption[] SecurityOptions =
        [new("SSL/TLS", 2), new("STARTTLS", 3), new("Automatic", 1), new("None", 0)];

    private readonly MailAccount? _existing;
    private string _color;
    private string? _detectedFor;
    private CancellationTokenSource? _detectCts;

    public MailAccount? Result { get; private set; }

    public AccountWindow(MailAccount? existing)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _existing = existing;

        ImapSecurityBox.ItemsSource = SecurityOptions;
        SmtpSecurityBox.ItemsSource = SecurityOptions;
        Swatches.ItemsSource = Palette.AccountPalette;

        var used = MailStore.GetAccounts().Select(a => a.Color).ToHashSet();
        _color = existing?.Color ?? Palette.AccountPalette.FirstOrDefault(c => !used.Contains(c)) ?? Palette.AccountPalette[0];

        if (existing != null)
        {
            Title = "Edit account";
            Heading.Text = "Edit account";
            SaveButton.Content = "Save";
            EmailBox.Text = existing.Email;
            DisplayNameBox.Text = existing.DisplayName;
            SenderNameBox.Text = existing.SenderName;
            SignatureBox.Text = existing.Signature;
            PasswordHint.Visibility = Visibility.Visible;
            Fill(new ServerConfig(existing.ImapHost, existing.ImapPort, existing.ImapSecurity, existing.SmtpHost, existing.SmtpPort, existing.SmtpSecurity, ""));
            UsernameBox.Text = existing.Username;
            _detectedFor = existing.Email;
            DetectText.Text = $"Incoming {existing.ImapHost}:{existing.ImapPort} · Outgoing {existing.SmtpHost}:{existing.SmtpPort}";
        }
        else
        {
            ImapSecurityBox.SelectedIndex = 0;
            SmtpSecurityBox.SelectedIndex = 0;
        }

        Loaded += (_, _) =>
        {
            SelectSwatch();
            EmailBox.Focus();
        };
    }

    private void SelectSwatch()
    {
        foreach (var item in Swatches.Items)
        {
            if (Swatches.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter cp
                && VisualTreeHelperFirst<RadioButton>(cp) is { } rb && (string)rb.Tag == _color)
                rb.IsChecked = true;
        }
    }

    private static T? VisualTreeHelperFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (VisualTreeHelperFirst<T>(child) is { } found) return found;
        }
        return null;
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e) => _color = (string)((RadioButton)sender).Tag;

    private void AdvancedToggle_Changed(object sender, RoutedEventArgs e) =>
        AdvancedPanel.Visibility = AdvancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void EmailBox_LostFocus(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task<bool> DetectAsync()
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') || email == _detectedFor) return ImapHostBox.Text.Length > 0;

        _detectCts?.Cancel();
        _detectCts = new CancellationTokenSource();
        DetectText.Text = "Looking up server settings…";
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text)) DisplayNameBox.Text = email[(email.IndexOf('@') + 1)..].Split('.')[0] is var d && d.Length > 0 ? char.ToUpper(d[0]) + d[1..] : "";
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || UsernameBox.Text == _detectedFor) UsernameBox.Text = email;

        try
        {
            var config = await AutoConfig.DiscoverAsync(email, _detectCts.Token);
            if (EmailBox.Text.Trim() != email) return false;
            _detectedFor = email;
            if (config == null)
            {
                DetectText.Text = "Couldn't detect the servers automatically. Enter them under Advanced.";
                AdvancedToggle.IsChecked = true;
                return false;
            }
            Fill(config);
            DetectText.Text = $"{config.Provider}: {config.ImapHost} / {config.SmtpHost}";
            ProviderNote.Text = config.Note ?? "";
            ProviderNote.Visibility = string.IsNullOrEmpty(config.Note) ? Visibility.Collapsed : Visibility.Visible;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Fill(ServerConfig c)
    {
        ImapHostBox.Text = c.ImapHost;
        ImapPortBox.Text = c.ImapPort.ToString();
        ImapSecurityBox.SelectedItem = SecurityOptions.FirstOrDefault(o => o.Value == c.ImapSecurity) ?? SecurityOptions[0];
        SmtpHostBox.Text = c.SmtpHost;
        SmtpPortBox.Text = c.SmtpPort.ToString();
        SmtpSecurityBox.SelectedItem = SecurityOptions.FirstOrDefault(o => o.Value == c.SmtpSecurity) ?? SecurityOptions[0];
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@')) { ShowError("Enter a valid email address."); return; }
        var password = PasswordBox.Password;
        if (_existing == null && password.Length == 0) { ShowError("Enter your password (or an app password)."); return; }

        SetBusy(true, "Finding your servers…");
        try
        {
            if (string.IsNullOrWhiteSpace(ImapHostBox.Text) && !await DetectAsync())
            {
                ShowError("Enter the server settings under Advanced.");
                return;
            }

            if (!int.TryParse(ImapPortBox.Text, out var imapPort) || !int.TryParse(SmtpPortBox.Text, out var smtpPort))
            {
                ShowError("Ports must be numbers.");
                AdvancedToggle.IsChecked = true;
                return;
            }

            var account = _existing?.Clone() ?? new MailAccount();
            account.Email = email;
            account.DisplayName = DisplayNameBox.Text.Trim();
            account.SenderName = SenderNameBox.Text.Trim();
            account.Signature = SignatureBox.Text.TrimEnd();
            account.Color = _color;
            account.Username = string.IsNullOrWhiteSpace(UsernameBox.Text) ? email : UsernameBox.Text.Trim();
            account.ImapHost = ImapHostBox.Text.Trim();
            account.ImapPort = imapPort;
            account.ImapSecurity = ((SecurityOption)ImapSecurityBox.SelectedItem).Value;
            account.SmtpHost = SmtpHostBox.Text.Trim();
            account.SmtpPort = smtpPort;
            account.SmtpSecurity = ((SecurityOption)SmtpSecurityBox.SelectedItem).Value;

            var effectivePassword = password.Length > 0 ? password : Secrets.Unprotect(account.ProtectedPassword);
            bool serverChanged = _existing == null || password.Length > 0 || account.ImapHost != _existing.ImapHost || account.SmtpHost != _existing.SmtpHost
                                 || account.ImapPort != _existing.ImapPort || account.SmtpPort != _existing.SmtpPort || account.Username != _existing.Username
                                 || account.ImapSecurity != _existing.ImapSecurity || account.SmtpSecurity != _existing.SmtpSecurity;
            if (serverChanged)
            {
                SetBusy(true, "Signing in…");
                await AccountSession.TestAsync(account, effectivePassword);
            }

            if (password.Length > 0) account.ProtectedPassword = Secrets.Protect(password);
            if (_existing == null) MailStore.InsertAccount(account);
            else MailStore.UpdateAccount(account);

            Result = account;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            AdvancedToggle.IsChecked = true;
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, string text)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text;
        SaveButton.IsEnabled = !busy;
    }
}
