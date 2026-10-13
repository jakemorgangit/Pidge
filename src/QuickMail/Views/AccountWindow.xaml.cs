using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>An entry in "Use servers from": auto-detect, a saved template, or an existing account's servers.</summary>
    private record Preset(string Label, ServerConfig? Config)
    {
        public override string ToString() => Label;
    }

    private record Guide(string Title, string Intro, string Step1, string Step1Label, string Step1Url,
        string Step2, string Step2Label, string Step2Url, string PasswordLabel);

    private static readonly Dictionary<ProviderKind, Guide> Guides = new()
    {
        [ProviderKind.Google] = new(
            "Connect Gmail with an app password",
            "Google doesn't let mail apps use your normal password. You'll create a 16-character app password just for QuickMail. It takes about a minute.",
            "App passwords need 2-Step Verification. Check it's switched on.", "Check 2-Step Verification",
            "https://myaccount.google.com/signinoptions/twosv",
            "Create an app password. Type QuickMail as the name and click Create.", "Open Google app passwords",
            "https://myaccount.google.com/apppasswords",
            "App password (16 letters)"),
        [ProviderKind.Yahoo] = new(
            "Connect Yahoo with an app password",
            "Yahoo needs a separate app password for mail apps.",
            "Open your Yahoo account security page.", "Open Yahoo security",
            "https://login.yahoo.com/account/security",
            "Choose Generate app password, name it QuickMail and click Generate.", "Generate an app password",
            "https://login.yahoo.com/myaccount/security/app-password",
            "App password"),
        [ProviderKind.ICloud] = new(
            "Connect iCloud with an app-specific password",
            "Apple needs an app-specific password for mail apps, and two-factor authentication must be on.",
            "Sign in to your Apple Account.", "Open Apple Account",
            "https://account.apple.com/",
            "Go to Sign-In and Security → App-Specific Passwords, add one named QuickMail.", "Open Sign-In and Security",
            "https://account.apple.com/account/manage/section/security",
            "App-specific password")
    };

    private static readonly SecurityOption[] SecurityOptions =
        [new("SSL/TLS", 2), new("STARTTLS", 3), new("Automatic", 1), new("None", 0)];

    private readonly MailAccount? _existing;
    private string _color;
    private string? _detectedFor;
    private CancellationTokenSource? _detectCts;
    private ProviderKind _provider = ProviderKind.Other;
    private Guide? _guide;
    private bool _fillingPreset;
    private MicrosoftAuth.SignInResult? _microsoft;

    public MailAccount? Result { get; private set; }

    public AccountWindow(MailAccount? existing)
    {
        InitializeComponent();
        WindowTheme.ApplyDark(this);
        _existing = existing;

        ImapSecurityBox.ItemsSource = SecurityOptions;
        SmtpSecurityBox.ItemsSource = SecurityOptions;
        Swatches.ItemsSource = Palette.AccountPalette;
        LoadPresets();

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
            SetProvider(existing.AuthMethod == AuthMethod.MicrosoftOAuth ? ProviderKind.Microsoft : ProviderKind.Other);
            if (existing.AuthMethod == AuthMethod.MicrosoftOAuth) MicrosoftStatus.Text = "Signed in. Sign in again only if mail stops syncing.";
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

    // ---------------- presets / templates ----------------
    private void LoadPresets()
    {
        var presets = new List<Preset> { new("Detected automatically", null) };
        foreach (var t in MailStore.GetTemplates())
            presets.Add(new($"Template: {t.Name}", new ServerConfig(t.ImapHost, t.ImapPort, t.ImapSecurity, t.SmtpHost, t.SmtpPort, t.SmtpSecurity, t.Name)));
        foreach (var g in MailStore.GetAccounts().Where(a => a.AuthMethod == AuthMethod.Password && a.Id != _existing?.Id)
                     .GroupBy(a => (a.ImapHost.ToLowerInvariant(), a.SmtpHost.ToLowerInvariant())))
        {
            var a = g.First();
            var name = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Email : a.DisplayName;
            presets.Add(new($"Same as {name} ({a.ImapHost})",
                new ServerConfig(a.ImapHost, a.ImapPort, a.ImapSecurity, a.SmtpHost, a.SmtpPort, a.SmtpSecurity, "Same as " + name)));
        }
        PresetBox.ItemsSource = presets;
        PresetBox.SelectedIndex = 0;
        PresetRow.Visibility = presets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingPreset || PresetBox.SelectedItem is not Preset { Config: { } config }) return;
        Fill(config);
        _detectedFor = EmailBox.Text.Trim();
        SetProvider(ProviderKind.Other);
        DetectText.Text = $"{config.Provider}: {config.ImapHost} / {config.SmtpHost}";
    }

    // ---------------- provider-specific guidance ----------------
    private void SetProvider(ProviderKind kind)
    {
        _provider = kind;
        MicrosoftPanel.Visibility = kind == ProviderKind.Microsoft ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = kind == ProviderKind.Microsoft ? Visibility.Collapsed : Visibility.Visible;
        MicrosoftNotConfigured.Visibility = kind == ProviderKind.Microsoft && !MicrosoftAuth.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        MicrosoftButton.IsEnabled = MicrosoftAuth.IsConfigured;

        _guide = Guides.GetValueOrDefault(kind);
        GuidePanel.Visibility = _guide != null && _existing == null ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Text = _guide?.PasswordLabel ?? "Password";
        if (_guide == null) return;
        GuideTitle.Text = _guide.Title;
        GuideIntro.Text = _guide.Intro + (EmailBox.Text.Contains("@gmail.") || EmailBox.Text.Contains("@googlemail.") || kind != ProviderKind.Google
            ? "" : " (Work or school Google accounts: your administrator must allow app passwords.)");
        Step1Text.Text = _guide.Step1;
        Step1Label.Text = _guide.Step1Label;
        Step2Text.Text = _guide.Step2;
        Step2Label.Text = _guide.Step2Label;
    }

    private void Step1_Click(object sender, RoutedEventArgs e) => OpenUrl(_guide?.Step1Url);
    private void Step2_Click(object sender, RoutedEventArgs e) => OpenUrl(_guide?.Step2Url);

    private static void OpenUrl(string? url)
    {
        if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        if (text.Length == 0) return;
        PasswordBox.Password = _provider == ProviderKind.Google ? text.Replace(" ", "") : text;
        PasswordBox.Focus();
    }

    private async void MicrosoftSignIn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        MicrosoftButton.IsEnabled = false;
        MicrosoftStatus.Foreground = (System.Windows.Media.Brush)FindResource("SubBrush");
        MicrosoftStatus.Text = "Waiting for you to finish signing in in your browser…";
        try
        {
            _microsoft = await MicrosoftAuth.SignInAsync(EmailBox.Text.Trim(), CancellationToken.None);
            if (string.IsNullOrWhiteSpace(EmailBox.Text)) EmailBox.Text = _microsoft.Email;
            MicrosoftStatus.Foreground = (System.Windows.Media.Brush)FindResource("OkBrush");
            MicrosoftStatus.Text = $"Signed in as {_microsoft.Email}";
            Activate();
        }
        catch (Exception ex)
        {
            MicrosoftStatus.Text = "";
            ShowError(ex is OperationCanceledException ? "Sign-in timed out. Try again." : ex.Message);
        }
        finally
        {
            MicrosoftButton.IsEnabled = MicrosoftAuth.IsConfigured;
        }
    }

    // ---------------- colours ----------------
    private void SelectSwatch()
    {
        foreach (var item in Swatches.Items)
        {
            if (Swatches.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter cp
                && FindChild<RadioButton>(cp) is { } rb && (string)rb.Tag == _color)
                rb.IsChecked = true;
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e) => _color = (string)((RadioButton)sender).Tag;

    private void AdvancedToggle_Changed(object sender, RoutedEventArgs e) =>
        AdvancedPanel.Visibility = AdvancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- detection ----------------
    private async void EmailBox_LostFocus(object sender, RoutedEventArgs e) => await DetectAsync();

    private async void EmailBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await DetectAsync(); }
    }

    private async Task<bool> DetectAsync()
    {
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@') || email == _detectedFor) return ImapHostBox.Text.Length > 0;

        _detectCts?.Cancel();
        _detectCts = new CancellationTokenSource();
        DetectText.Text = "Looking up server settings…";
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text))
        {
            var d = email[(email.IndexOf('@') + 1)..].Split('.')[0];
            DisplayNameBox.Text = d.Length > 0 ? char.ToUpper(d[0]) + d[1..] : "";
        }
        if (string.IsNullOrWhiteSpace(UsernameBox.Text) || UsernameBox.Text == _detectedFor) UsernameBox.Text = email;

        try
        {
            var config = await AutoConfig.DiscoverAsync(email, _detectCts.Token);
            if (EmailBox.Text.Trim() != email) return false;
            _detectedFor = email;
            if (config == null)
            {
                DetectText.Text = PresetRow.Visibility == Visibility.Visible
                    ? "Couldn't detect the servers. Pick saved servers below, or enter them under Advanced."
                    : "Couldn't detect the servers automatically. Enter them under Advanced.";
                if (PresetRow.Visibility != Visibility.Visible) AdvancedToggle.IsChecked = true;
                SetProvider(ProviderKind.Other);
                return false;
            }
            _fillingPreset = true;
            PresetBox.SelectedIndex = 0;
            _fillingPreset = false;
            Fill(config);
            DetectText.Text = $"{config.Provider}: {config.ImapHost} / {config.SmtpHost}";
            SetProvider(config.Kind);
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

    // ---------------- save ----------------
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var email = EmailBox.Text.Trim();
        if (!email.Contains('@')) { ShowError("Enter a valid email address."); return; }

        SetBusy(true, "Finding your servers…");
        try
        {
            if (string.IsNullOrWhiteSpace(ImapHostBox.Text) && !await DetectAsync())
            {
                ShowError("Enter the server settings under Advanced, or pick saved servers.");
                return;
            }

            bool microsoft = _provider == ProviderKind.Microsoft;
            var password = PasswordBox.Password;
            if (_provider == ProviderKind.Google) password = password.Replace(" ", "");
            if (microsoft && _microsoft == null && _existing?.AuthMethod != AuthMethod.MicrosoftOAuth)
            {
                ShowError("Click Sign in with Microsoft first.");
                return;
            }
            if (!microsoft && _existing == null && password.Length == 0)
            {
                ShowError(_guide != null ? "Follow the steps above and paste your app password." : "Enter your password.");
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
            account.Username = microsoft ? (_microsoft?.Email is { Length: > 0 } m ? m : email)
                : string.IsNullOrWhiteSpace(UsernameBox.Text) ? email : UsernameBox.Text.Trim();
            account.ImapHost = ImapHostBox.Text.Trim();
            account.ImapPort = imapPort;
            account.ImapSecurity = ((SecurityOption)ImapSecurityBox.SelectedItem).Value;
            account.SmtpHost = SmtpHostBox.Text.Trim();
            account.SmtpPort = smtpPort;
            account.SmtpSecurity = ((SecurityOption)SmtpSecurityBox.SelectedItem).Value;
            account.AuthMethod = microsoft ? AuthMethod.MicrosoftOAuth : AuthMethod.Password;

            string? secretForTest;
            if (microsoft)
            {
                if (_microsoft != null) account.ProtectedPassword = Secrets.Protect(_microsoft.RefreshToken);
                secretForTest = _microsoft?.AccessToken;
            }
            else
            {
                if (password.Length > 0) account.ProtectedPassword = Secrets.Protect(password);
                secretForTest = password.Length > 0 ? password : Secrets.Unprotect(account.ProtectedPassword);
            }

            bool changed = _existing == null || password.Length > 0 || _microsoft != null || account.ImapHost != _existing.ImapHost
                           || account.SmtpHost != _existing.SmtpHost || account.ImapPort != _existing.ImapPort || account.SmtpPort != _existing.SmtpPort
                           || account.Username != _existing.Username || account.ImapSecurity != _existing.ImapSecurity
                           || account.SmtpSecurity != _existing.SmtpSecurity || account.AuthMethod != _existing.AuthMethod;
            if (changed && secretForTest != null)
            {
                SetBusy(true, "Signing in…");
                await AccountSession.TestAsync(account, secretForTest);
            }

            if (SaveTemplateBox.IsChecked == true && !string.IsNullOrWhiteSpace(TemplateNameBox.Text))
            {
                MailStore.SaveTemplate(new ServerTemplate
                {
                    Name = TemplateNameBox.Text.Trim(), ImapHost = account.ImapHost, ImapPort = account.ImapPort, ImapSecurity = account.ImapSecurity,
                    SmtpHost = account.SmtpHost, SmtpPort = account.SmtpPort, SmtpSecurity = account.SmtpSecurity
                });
            }

            if (_existing == null) MailStore.InsertAccount(account);
            else MailStore.UpdateAccount(account);
            MicrosoftAuth.Forget(account.Id);

            Result = account;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message + (_guide != null && ex.Message.Contains("password") ? " Make sure you pasted the app password, not your normal password." : ""));
            if (_provider == ProviderKind.Other) AdvancedToggle.IsChecked = true;
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
