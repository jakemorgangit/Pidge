using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.WinUI.Notifications;
using Pidge.Data;
using Pidge.Helpers;
using Pidge.Models;

namespace Pidge.Services;

public enum NotificationAction { Open, MarkRead, Archive, Delete }

/// <summary>
/// Windows notifications for new mail. Respects the app's own mute settings (everything, per account, or for a while),
/// skips suspected spam and, optionally, newsletters and automated mail. Windows Focus Assist / Do Not Disturb also applies.
/// </summary>
public static class Notifier
{
    private const int MaxIndividualToasts = 3;

    /// <summary>Raised (on a background thread) when the user clicks a notification or one of its buttons.</summary>
    public static event Action<NotificationAction, long>? Activated;

    private static string AvatarDir => Path.Combine(Db.DataDir, "avatars");

    public static void Initialize()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue("id", out var idText) || !long.TryParse(idText, out var id)) id = 0;
            var action = args.TryGetValue("action", out var a) ? a : "open";
            Activated?.Invoke(action switch
            {
                "read" => NotificationAction.MarkRead,
                "archive" => NotificationAction.Archive,
                "delete" => NotificationAction.Delete,
                _ => NotificationAction.Open
            }, id);
        };
    }

    // ---------------- mute state ----------------
    public static bool IsPaused => AppSettings.Current.NotificationsPausedUntilUtc is { } until && until > DateTime.UtcNow;

    public static bool IsAccountMuted(MailAccount account) => AppSettings.Current.MutedAccounts.Contains(account.AccountGuid);

    public static void SetAccountMuted(MailAccount account, bool muted)
    {
        var list = AppSettings.Current.MutedAccounts;
        list.Remove(account.AccountGuid);
        if (muted) list.Add(account.AccountGuid);
        AppSettings.Save();
    }

    public static void PauseUntil(DateTime? utc)
    {
        AppSettings.Current.NotificationsPausedUntilUtc = utc;
        AppSettings.Save();
    }

    public static string PauseDescription
    {
        get
        {
            if (!AppSettings.Current.NotificationsEnabled) return "Notifications are off";
            if (AppSettings.Current.NotificationsPausedUntilUtc is not { } until || until <= DateTime.UtcNow) return "Notifications on";
            if (until >= DateTime.MaxValue.AddYears(-1)) return "Notifications paused";
            var local = until.ToLocalTime();
            return local.Date == DateTime.Today
                ? $"Notifications paused until {local:HH:mm}"
                : $"Notifications paused until {local.ToString("ddd HH:mm", CultureInfo.CurrentCulture)}";
        }
    }

    /// <summary>Which of these new messages deserve a notification, given the user's settings.</summary>
    public static List<MessageRecord> Filter(MailAccount account, IEnumerable<MessageRecord> fresh)
    {
        var s = AppSettings.Current;
        if (!s.NotificationsEnabled || IsPaused || IsAccountMuted(account)) return [];
        return fresh.Where(m => !m.IsSpam && !m.IsRead)
            .Where(m => !s.NotifyPeopleOnly || m.Category == MailCategory.Personal || SpamEngine.IsKnownCorrespondent(m.FromAddress))
            .OrderByDescending(m => m.DateUnix)
            .ToList();
    }

    // ---------------- showing ----------------
    /// <summary>Shows notifications for new mail in one account. Call on the UI thread.</summary>
    public static void ShowNewMail(MailAccount account, IReadOnlyList<MessageRecord> fresh)
    {
        var list = Filter(account, fresh);
        Log.Info($"New mail in {account.Email}: {fresh.Count} new, {list.Count} to notify (enabled={AppSettings.Current.NotificationsEnabled}, paused={IsPaused}, muted={IsAccountMuted(account)})");
        if (list.Count == 0) return;
        var accountName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName;

        try
        {
            if (list.Count <= MaxIndividualToasts)
            {
                foreach (var m in list.AsEnumerable().Reverse()) ShowOne(m, accountName);
            }
            else
            {
                var senders = list.Select(m => string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName).Distinct().Take(3);
                var builder = new ToastContentBuilder()
                    .AddArgument("action", "open")
                    .AddArgument("id", list[0].Id)
                    .AddText($"{list.Count} new emails")
                    .AddText(string.Join(", ", senders) + (list.Count > 3 ? " and others" : ""))
                    .AddAttributionText(accountName);
                if (!AppSettings.Current.NotifySound) builder.AddAudio(new ToastAudio { Silent = true });
                builder.Show(t =>
                {
                    t.Tag = "summary";
                    t.Group = account.Id.ToString(CultureInfo.InvariantCulture);
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't show a notification", ex); // best-effort (e.g. disabled by policy)
        }
    }

    private static void ShowOne(MessageRecord m, string accountName)
    {
        var sender = string.IsNullOrWhiteSpace(m.FromName) ? m.FromAddress : m.FromName;
        var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
        var preview = m.Preview.Length > 140 ? m.Preview[..140] + "…" : m.Preview;

        var builder = new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddArgument("id", m.Id)
            .AddText(sender)
            .AddText(subject)
            .AddAttributionText(accountName)
            .AddButton(new ToastButton().SetContent("Mark as read").AddArgument("action", "read").AddArgument("id", m.Id).SetBackgroundActivation())
            .AddButton(new ToastButton().SetContent("Archive").AddArgument("action", "archive").AddArgument("id", m.Id).SetBackgroundActivation())
            .AddButton(new ToastButton().SetContent("Delete").AddArgument("action", "delete").AddArgument("id", m.Id).SetBackgroundActivation());
        if (preview.Length > 0) builder.AddText(preview);
        // Company logo for organisations (when enabled), otherwise a round initials avatar.
        if (AppSettings.Current.ShowBrandLogos && BrandService.Lookup(m.FromAddress)?.IconPath is { } logo)
            builder.AddAppLogoOverride(new Uri(logo), ToastGenericAppLogoCrop.Default);
        else if (AvatarFor(sender, m.FromAddress) is { } avatar)
            builder.AddAppLogoOverride(new Uri(avatar), ToastGenericAppLogoCrop.Circle);
        if (!AppSettings.Current.NotifySound) builder.AddAudio(new ToastAudio { Silent = true });
        builder.Show(t =>
        {
            t.Tag = m.Id.ToString(CultureInfo.InvariantCulture);
            t.Group = m.AccountId.ToString(CultureInfo.InvariantCulture);
        });
    }

    /// <summary>Removes a message's notification from Action Center (e.g. once it's been read in Pidge).</summary>
    public static void Dismiss(MessageRecord m)
    {
        try { ToastNotificationManagerCompat.History.Remove(m.Id.ToString(CultureInfo.InvariantCulture), m.AccountId.ToString(CultureInfo.InvariantCulture)); }
        catch { /* not shown, or notifications unavailable */ }
    }

    public static void ShowTest()
    {
        try
        {
            new ToastContentBuilder()
                .AddText("Pidge notifications are on")
                .AddText("New mail will appear here. You can mute everything or individual accounts from Pidge.")
                .Show();
        }
        catch { /* ignore */ }
    }

    /// <summary>Draws a round initials avatar (same colours as the app) and caches it as a PNG.</summary>
    private static string? AvatarFor(string name, string address)
    {
        try
        {
            Directory.CreateDirectory(AvatarDir);
            var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(address.ToLowerInvariant())))[..16];
            var path = Path.Combine(AvatarDir, key + ".png");
            if (File.Exists(path)) return path;

            const int size = 96;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawEllipse(Palette.ForText(address.Length > 0 ? address : name), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
                var initials = ViewModels.MessageItemViewModel.MakeInitials(name);
                var text = new FormattedText(initials, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 38, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
