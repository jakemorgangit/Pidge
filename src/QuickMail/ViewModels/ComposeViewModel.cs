using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public enum ComposeMode { New, Reply, ReplyAll, Forward, Draft }

public partial class ComposeViewModel : ObservableObject
{
    public ObservableCollection<MailAccount> Accounts { get; }
    public ObservableCollection<string> AttachmentPaths { get; } = new();

    [ObservableProperty] private MailAccount? _fromAccount;
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private bool _showCcBcc;
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string? _error;

    public string InitialHtml { get; private set; } = "";
    public string InReplyTo { get; private set; } = "";
    public string References { get; private set; } = "";
    public MessageRecord? ReplySource { get; private set; }
    /// <summary>The draft this message was opened from; removed once sent or re-saved.</summary>
    public MessageRecord? DraftSource { get; set; }
    public ComposeMode Mode { get; private set; }
    public bool IsDirty { get; set; }

    /// <summary>Supplied by the view: returns the editor's current HTML.</summary>
    public Func<Task<string>>? GetBodyHtml { get; set; }
    /// <summary>Raised when the From account changes so the view can swap the signature.</summary>
    public event Action<string>? SignatureChanged;
    public event Action? CloseRequested;

    public string WindowTitle => Mode switch
    {
        ComposeMode.Reply or ComposeMode.ReplyAll => "Reply",
        ComposeMode.Forward => "Forward",
        _ => "New message"
    };

    private ComposeViewModel(IEnumerable<MailAccount> accounts, MailAccount? from, ComposeMode mode)
    {
        Accounts = new ObservableCollection<MailAccount>(accounts);
        _fromAccount = from ?? Accounts.FirstOrDefault();
        Mode = mode;
    }

    partial void OnFromAccountChanged(MailAccount? value) => SignatureChanged?.Invoke(SignatureHtml(value));

    private static string SignatureHtml(MailAccount? a) =>
        a == null || string.IsNullOrWhiteSpace(a.Signature) ? "" : "-- <br>" + HtmlTools.Encode(a.Signature).Replace("\n", "<br>");

    private static string Shell(MailAccount? from, string quote) =>
        $"<p><br></p><div class=\"qm-sig\">{SignatureHtml(from)}</div>{quote}";

    // ---------------- Factories ----------------
    public static ComposeViewModel New(IEnumerable<MailAccount> accounts, MailAccount? from, string to = "", string subject = "")
    {
        var vm = new ComposeViewModel(accounts, from, ComposeMode.New) { To = to, Subject = subject };
        vm.InitialHtml = Shell(vm.FromAccount, "");
        return vm;
    }

    public static ComposeViewModel Reply(IEnumerable<MailAccount> accounts, MailAccount from, MessageRecord source, MimeMessage original, bool all)
    {
        var vm = new ComposeViewModel(accounts, from, all ? ComposeMode.ReplyAll : ComposeMode.Reply) { ReplySource = source };
        var me = from.Email;

        var replyTo = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        var to = replyTo.ToList();
        var cc = new List<MailboxAddress>();
        if (all)
        {
            to.AddRange(original.To.Mailboxes);
            cc.AddRange(original.Cc.Mailboxes);
        }
        bool NotMe(MailboxAddress m) => !string.Equals(m.Address, me, StringComparison.OrdinalIgnoreCase);
        var toFinal = to.Where(NotMe).DistinctBy(m => m.Address.ToLowerInvariant()).ToList();
        if (toFinal.Count == 0) toFinal = replyTo.ToList(); // replying to yourself
        vm.To = string.Join(", ", toFinal.Select(m => m.ToString()));
        vm.Cc = string.Join(", ", cc.Where(NotMe).Where(c => toFinal.All(t => !t.Address.Equals(c.Address, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(m => m.Address.ToLowerInvariant()).Select(m => m.ToString()));
        vm.ShowCcBcc = vm.Cc.Length > 0;

        var subject = original.Subject ?? "";
        vm.Subject = subject.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? subject : "Re: " + subject;

        vm.InReplyTo = original.MessageId ?? "";
        var refs = original.References.Select(r => "<" + r + ">").ToList();
        if (!string.IsNullOrEmpty(original.MessageId)) refs.Add("<" + original.MessageId + ">");
        vm.References = string.Join(" ", refs);

        var who = HtmlTools.Encode(original.From.Mailboxes.FirstOrDefault()?.ToString() ?? "");
        var when = original.Date.LocalDateTime.ToString("ddd d MMM yyyy 'at' HH:mm");
        vm.InitialHtml = Shell(from,
            $"<div class=\"qm-quote\"><p style=\"color:#6b7280\">On {when}, {who} wrote:</p><blockquote style=\"border-left:3px solid #d0d4dc;margin:0;padding-left:12px\">{HtmlTools.QuotableBody(original)}</blockquote></div>");
        return vm;
    }

    public static ComposeViewModel Forward(IEnumerable<MailAccount> accounts, MailAccount from, MimeMessage original)
    {
        var vm = new ComposeViewModel(accounts, from, ComposeMode.Forward);
        var subject = original.Subject ?? "";
        vm.Subject = subject.StartsWith("fwd:", StringComparison.OrdinalIgnoreCase) ? subject : "Fwd: " + subject;

        var header = $"""
            <p style="color:#6b7280">---------- Forwarded message ----------<br>
            From: {HtmlTools.Encode(original.From.ToString())}<br>
            Date: {original.Date.LocalDateTime:ddd d MMM yyyy HH:mm}<br>
            Subject: {HtmlTools.Encode(original.Subject ?? "")}<br>
            To: {HtmlTools.Encode(original.To.ToString())}</p>
            """;
        vm.InitialHtml = Shell(from, $"<div class=\"qm-quote\">{header}{HtmlTools.QuotableBody(original)}</div>");

        var dir = Path.Combine(Path.GetTempPath(), "QuickMail", "fwd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var part in original.Attachments.OfType<MimePart>())
        {
            var path = Path.Combine(dir, SafeFileName(part.FileName ?? "attachment"));
            using (var fs = File.Create(path)) part.Content.DecodeTo(fs);
            vm.AttachmentPaths.Add(path);
        }
        return vm;
    }

    public static ComposeViewModel FromDraft(IEnumerable<MailAccount> accounts, MailAccount from, MimeMessage draft)
    {
        var vm = new ComposeViewModel(accounts, from, ComposeMode.Draft)
        {
            To = draft.To.ToString(),
            Cc = draft.Cc.ToString(),
            Bcc = draft.Bcc.ToString(),
            Subject = draft.Subject ?? "",
            InReplyTo = draft.InReplyTo ?? "",
            References = string.Join(" ", draft.References.Select(r => "<" + r + ">"))
        };
        vm.ShowCcBcc = vm.Cc.Length > 0 || vm.Bcc.Length > 0;
        vm.InitialHtml = HtmlTools.QuotableBody(draft);
        return vm;
    }

    public static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ---------------- Commands ----------------
    [RelayCommand]
    private void RemoveAttachment(string path) => AttachmentPaths.Remove(path);

    [RelayCommand]
    private void ToggleCcBcc() => ShowCcBcc = !ShowCcBcc;

    [RelayCommand]
    private async Task SendAsync()
    {
        Error = null;
        if (FromAccount == null) { Error = "Choose an account to send from."; return; }
        if (!TryParse(To, out var to) || !TryParse(Cc, out var cc) || !TryParse(Bcc, out var bcc))
        {
            Error = "One of the recipient addresses doesn't look right.";
            return;
        }
        if (to.Count + cc.Count + bcc.Count == 0) { Error = "Add at least one recipient."; return; }

        IsSending = true;
        try
        {
            var message = await BuildAsync(to, cc, bcc);
            await Task.Run(() => MailEngine.Instance.SendAsync(FromAccount.Id, message));
            if (ReplySource != null) _ = MailEngine.Instance.SetAnsweredAsync(ReplySource);
            DiscardDraftSource();
            IsDirty = false;
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Error = "Couldn't send: " + AccountSession.Friendly(ex);
        }
        finally
        {
            IsSending = false;
        }
    }

    public async Task<bool> SaveDraftAsync()
    {
        if (FromAccount == null) return false;
        TryParse(To, out var to);
        TryParse(Cc, out var cc);
        TryParse(Bcc, out var bcc);
        try
        {
            var message = await BuildAsync(to, cc, bcc);
            await Task.Run(() => MailEngine.Instance.SaveDraftAsync(FromAccount.Id, message));
            DiscardDraftSource();
            IsDirty = false;
            return true;
        }
        catch (Exception ex)
        {
            Error = "Couldn't save the draft: " + AccountSession.Friendly(ex);
            return false;
        }
    }

    private void DiscardDraftSource()
    {
        if (DraftSource == null) return;
        var old = DraftSource;
        DraftSource = null;
        _ = Task.Run(() => MailEngine.Instance.MoveAsync([old], FolderKind.Trash));
    }

    private async Task<MimeMessage> BuildAsync(InternetAddressList to, InternetAddressList cc, InternetAddressList bcc)
    {
        var html = GetBodyHtml != null ? await GetBodyHtml() : InitialHtml;
        var account = FromAccount!;
        var message = new MimeMessage();
        var name = string.IsNullOrWhiteSpace(account.SenderName) ? account.DisplayName : account.SenderName;
        message.From.Add(new MailboxAddress(name, account.Email));
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Bcc.AddRange(bcc);
        message.Subject = Subject;
        if (!string.IsNullOrEmpty(InReplyTo)) message.InReplyTo = InReplyTo.Trim('<', '>');
        foreach (var r in References.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            message.References.Add(r.Trim('<', '>'));

        var builder = new BodyBuilder
        {
            HtmlBody = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:'Segoe UI',Arial,sans-serif;font-size:14px;\">{html}</body></html>",
            TextBody = HtmlTools.HtmlToText(html)
        };
        foreach (var path in AttachmentPaths) builder.Attachments.Add(path);
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static bool TryParse(string text, out InternetAddressList list)
    {
        list = new InternetAddressList();
        text = text.Replace(';', ',').Trim().TrimEnd(',');
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!InternetAddressList.TryParse(text, out var parsed)) return false;
        if (parsed.Mailboxes.Any(m => !m.Address.Contains('@'))) return false;
        list = parsed;
        return true;
    }

    public static string Decode(string jsonString) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(jsonString) ?? "";

    public static string Encode(string s) => WebUtility.HtmlEncode(s);
}
