using System.Collections.Concurrent;
using System.IO;
using MailKit;
using MailKit.Search;
using MimeKit;
using QuickMail.Data;
using QuickMail.Models;
using MailStore = QuickMail.Data.MailStore;

namespace QuickMail.Services;

/// <summary>
/// Owns the per-account sessions, keeps the local cache in sync with the servers and performs
/// every server-side action (flags, moves, deletes, sending). Events fire on background threads.
/// </summary>
public sealed class MailEngine
{
    public static MailEngine Instance { get; } = new();

    private readonly ConcurrentDictionary<long, AccountSession> _sessions = new();
    private readonly ConcurrentDictionary<long, MailAccount> _accounts = new();
    private CancellationTokenSource? _loopCts;

    /// <summary>Local data for an account/folder changed.</summary>
    public event Action<long, FolderKind>? FolderChanged;
    /// <summary>Account sync started/stopped; error is null on success.</summary>
    public event Action<long, bool, string?>? SyncState;
    /// <summary>New, non-spam mail arrived (account, count, newest record).</summary>
    public event Action<long, int, MessageRecord?>? NewMail;

    private static readonly string[] ExtraHeaders =
        ["List-Unsubscribe", "List-Id", "Precedence", "Authentication-Results", "X-Spam-Flag", "X-Spam-Status"];

    private static readonly FetchRequest SummaryRequest = new(
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure | MessageSummaryItems.PreviewText |
        MessageSummaryItems.Size | MessageSummaryItems.References)
    {
        Headers = new HeaderSet(ExtraHeaders)
    };

    // ---------------- Accounts ----------------
    public void Register(MailAccount account)
    {
        _accounts[account.Id] = account;
        if (_sessions.TryGetValue(account.Id, out var s)) s.UpdateAccount(account);
        else _sessions[account.Id] = new AccountSession(account);
    }

    public void Unregister(long accountId)
    {
        _accounts.TryRemove(accountId, out _);
        if (_sessions.TryRemove(accountId, out var s)) s.Dispose();
    }

    public MailAccount? Account(long id) => _accounts.TryGetValue(id, out var a) ? a : null;
    private AccountSession Session(long id) => _sessions[id];

    // ---------------- Background loop ----------------
    public void StartBackgroundSync()
    {
        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await SyncAllAsync(FolderKind.Inbox, ct);
                try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, AppSettings.Current.SyncIntervalMinutes)), ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public void Stop() => _loopCts?.Cancel();

    public Task SyncAllAsync(FolderKind kind, CancellationToken ct = default) =>
        Task.WhenAll(_accounts.Values.ToList().Select(a => SyncFolderAsync(a.Id, kind, ct)));

    // ---------------- Sync ----------------
    public async Task<int> SyncFolderAsync(long accountId, FolderKind kind, CancellationToken ct = default)
    {
        if (!_accounts.TryGetValue(accountId, out var account)) return 0;
        SyncState?.Invoke(accountId, true, null);
        try
        {
            var (added, newest) = await Session(accountId).RunAsync(c => SyncCoreAsync(c, account, kind, ct), ct);
            if (kind == FolderKind.Inbox) MailStore.SetLastSync(accountId);
            SyncState?.Invoke(accountId, false, null);
            FolderChanged?.Invoke(accountId, kind);
            if (added > 0 && kind == FolderKind.Inbox) NewMail?.Invoke(accountId, added, newest);
            return added;
        }
        catch (OperationCanceledException)
        {
            SyncState?.Invoke(accountId, false, null);
            return 0;
        }
        catch (Exception ex)
        {
            SyncState?.Invoke(accountId, false, AccountSession.Friendly(ex));
            return 0;
        }
    }

    private async Task<(int added, MessageRecord? newest)> SyncCoreAsync(MailKit.Net.Imap.ImapClient client, MailAccount account, FolderKind kind, CancellationToken ct)
    {
        var session = Session(account.Id);
        var folder = await session.GetFolderAsync(client, kind, ct);
        if (folder == null) return (0, null);

        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        long validity = folder.UidValidity;
        var localValidity = MailStore.GetUidValidity(account.Id, kind);
        if (localValidity != 0 && localValidity != validity) MailStore.ClearFolder(account.Id, kind);

        long maxUid = MailStore.GetMaxUid(account.Id, kind);
        IList<IMessageSummary> summaries = [];
        if (folder.Count > 0)
        {
            if (maxUid == 0)
            {
                int start = Math.Max(0, folder.Count - AppSettings.Current.InitialSyncCount);
                summaries = await folder.FetchAsync(start, -1, SummaryRequest, ct);
            }
            else
            {
                var range = new UniqueIdRange(new UniqueId((uint)maxUid + 1), UniqueId.MaxValue);
                summaries = (await folder.FetchAsync(range, SummaryRequest, ct)).Where(s => s.UniqueId.Id > maxUid).ToList();
            }
        }

        var (records, toTrash, toJunk) = Process(account, kind, validity, summaries);
        if (records.Count > 0)
        {
            MailStore.InsertMessages(records);
            if (kind == FolderKind.Inbox) MailStore.RecordReceived(records);
        }

        if (toTrash.Count > 0) await MoveOnServerAsync(client, session, folder, toTrash, FolderKind.Trash, ct);
        if (toJunk.Count > 0) await MoveOnServerAsync(client, session, folder, toJunk, FolderKind.Junk, ct);

        if (maxUid > 0) await SyncFlagsAsync(folder, account.Id, kind, maxUid, ct);
        await folder.CloseAsync(false, ct);

        var fresh = records.Where(r => !r.IsRead && !r.IsSpam).ToList();
        return (maxUid == 0 ? 0 : fresh.Count, fresh.OrderByDescending(r => r.DateUnix).FirstOrDefault());
    }

    /// <summary>Builds records and applies the block list and spam filter to newly fetched mail.</summary>
    private static (List<MessageRecord> records, List<UniqueId> toTrash, List<UniqueId> toJunk) Process(
        MailAccount account, FolderKind kind, long validity, IEnumerable<IMessageSummary> summaries)
    {
        var records = new List<MessageRecord>();
        var toTrash = new List<UniqueId>();
        var toJunk = new List<UniqueId>();

        foreach (var s in summaries)
        {
            var rec = Build(account.Id, kind, validity, s);
            if (kind == FolderKind.Inbox)
            {
                var blocked = SpamEngine.MatchBlocked(rec.FromAddress);
                if (blocked != null)
                {
                    toTrash.Add(s.UniqueId);
                    MailStore.BlockedHit(blocked);
                    MailStore.AddCounter("blocked", 1);
                    continue;
                }

                rec.Category = SpamEngine.Categorize(rec, s.Headers);
                var (score, reasons) = SpamEngine.Score(rec, s.Headers, account.Email);
                rec.SpamScore = score;
                rec.SpamReasons = reasons;
                rec.IsSpam = score >= AppSettings.Current.SpamThreshold;
                if (rec.IsSpam)
                {
                    MailStore.AddCounter("spamCaught", 1);
                    if (AppSettings.Current.MoveSpamToJunk)
                    {
                        toJunk.Add(s.UniqueId);
                        continue;
                    }
                }
            }
            else
            {
                rec.Category = SpamEngine.Categorize(rec, s.Headers);
            }
            records.Add(rec);
        }
        return (records, toTrash, toJunk);
    }

    private static MessageRecord Build(long accountId, FolderKind kind, long validity, IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From?.Mailboxes.FirstOrDefault();
        var flags = s.Flags ?? MessageFlags.None;
        var date = env?.Date ?? s.InternalDate ?? DateTimeOffset.Now;
        return new MessageRecord
        {
            AccountId = accountId,
            Folder = kind,
            Uid = s.UniqueId.Id,
            UidValidity = validity,
            MessageId = env?.MessageId ?? "",
            InReplyTo = env?.InReplyTo ?? "",
            Refs = s.References != null ? string.Join(" ", s.References.Select(r => "<" + r + ">")) : "",
            Subject = env?.Subject ?? "",
            FromName = from?.Name ?? "",
            FromAddress = from?.Address ?? "",
            ReplyTo = env?.ReplyTo?.Mailboxes.FirstOrDefault()?.Address ?? "",
            ToList = env?.To?.ToString() ?? "",
            CcList = env?.Cc?.ToString() ?? "",
            DateUnix = date.ToUnixTimeSeconds(),
            Preview = (s.PreviewText ?? "").Trim(),
            IsRead = flags.HasFlag(MessageFlags.Seen),
            IsFlagged = flags.HasFlag(MessageFlags.Flagged),
            IsAnswered = flags.HasFlag(MessageFlags.Answered),
            HasAttachments = s.Attachments?.Any() == true,
            ListUnsubscribe = s.Headers?["List-Unsubscribe"] ?? "",
            Size = s.Size ?? 0
        };
    }

    private static async Task SyncFlagsAsync(IMailFolder folder, long accountId, FolderKind kind, long maxUid, CancellationToken ct)
    {
        long minUid = MailStore.GetMinUid(accountId, kind);
        if (minUid == 0) return;
        var local = MailStore.GetLocalFlags(accountId, kind, minUid);
        var range = new UniqueIdRange(new UniqueId((uint)minUid), new UniqueId((uint)maxUid));
        var remote = await folder.FetchAsync(range, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct);

        var seen = new HashSet<long>();
        var changes = new List<(long, bool, bool, bool)>();
        foreach (var r in remote)
        {
            long uid = r.UniqueId.Id;
            seen.Add(uid);
            if (!local.TryGetValue(uid, out var l)) continue;
            var f = r.Flags ?? MessageFlags.None;
            var now = (f.HasFlag(MessageFlags.Seen), f.HasFlag(MessageFlags.Flagged), f.HasFlag(MessageFlags.Answered));
            if (now != l) changes.Add((uid, now.Item1, now.Item2, now.Item3));
        }
        var vanished = local.Keys.Where(k => !seen.Contains(k)).ToList();
        if (changes.Count > 0 || vanished.Count > 0) MailStore.ApplyFlagChanges(accountId, kind, changes, vanished);
    }

    /// <summary>Fetches older messages than the oldest one cached.</summary>
    public async Task<int> LoadOlderAsync(long accountId, FolderKind kind, int count = 200)
    {
        if (!_accounts.TryGetValue(accountId, out var account)) return 0;
        var session = Session(accountId);
        var added = await session.RunAsync(async c =>
        {
            var folder = await session.GetFolderAsync(c, kind);
            if (folder == null) return 0;
            await folder.OpenAsync(FolderAccess.ReadOnly);
            long minUid = MailStore.GetMinUid(accountId, kind);
            if (minUid <= 1) return 0;
            var uids = await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(UniqueId.MinValue, new UniqueId((uint)minUid - 1))));
            var page = uids.OrderByDescending(u => u.Id).Take(count).ToList();
            if (page.Count == 0) return 0;
            var summaries = await folder.FetchAsync(page, SummaryRequest);
            var (records, _, _) = Process(account, kind, folder.UidValidity, summaries);
            MailStore.InsertMessages(records);
            await folder.CloseAsync(false);
            return records.Count;
        });
        FolderChanged?.Invoke(accountId, kind);
        return added;
    }

    // ---------------- Message actions ----------------
    public async Task<MimeMessage> GetFullMessageAsync(MessageRecord m)
    {
        var path = CachePath(m);
        if (File.Exists(path))
        {
            try { return await MimeMessage.LoadAsync(path); } catch { File.Delete(path); }
        }

        var session = Session(m.AccountId);
        var message = await session.RunAsync(async c =>
        {
            var folder = await session.GetFolderAsync(c, m.Folder) ?? throw new InvalidOperationException("Folder not found on server.");
            await folder.OpenAsync(FolderAccess.ReadOnly);
            var msg = await folder.GetMessageAsync(new UniqueId((uint)m.Uid));
            await folder.CloseAsync(false);
            return msg;
        });

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await message.WriteToAsync(path);
        return message;
    }

    private static string CachePath(MessageRecord m) =>
        Path.Combine(Db.CacheDir, m.AccountId.ToString(), m.Folder.ToString(), $"{m.UidValidity}_{m.Uid}.eml");

    public Task SetReadAsync(IEnumerable<MessageRecord> messages, bool read)
    {
        foreach (var m in messages) { m.IsRead = read; MailStore.SetRead(m.Id, read); }
        return StoreFlagAsync(messages, MessageFlags.Seen, read);
    }

    public Task SetFlaggedAsync(IEnumerable<MessageRecord> messages, bool flagged)
    {
        foreach (var m in messages) { m.IsFlagged = flagged; MailStore.SetFlagged(m.Id, flagged); }
        return StoreFlagAsync(messages, MessageFlags.Flagged, flagged);
    }

    public Task SetAnsweredAsync(MessageRecord m)
    {
        m.IsAnswered = true;
        MailStore.SetAnswered(m.Id);
        return StoreFlagAsync([m], MessageFlags.Answered, true);
    }

    private async Task StoreFlagAsync(IEnumerable<MessageRecord> messages, MessageFlags flag, bool add)
    {
        foreach (var group in messages.GroupBy(m => (m.AccountId, m.Folder)).ToList())
        {
            if (!_sessions.TryGetValue(group.Key.AccountId, out var session)) continue;
            var uids = group.Select(m => new UniqueId((uint)m.Uid)).ToList();
            try
            {
                await session.RunAsync(async c =>
                {
                    var folder = await session.GetFolderAsync(c, group.Key.Folder);
                    if (folder == null) return;
                    await folder.OpenAsync(FolderAccess.ReadWrite);
                    await folder.StoreAsync(uids, new StoreFlagsRequest(add ? StoreAction.Add : StoreAction.Remove, flag) { Silent = true });
                    await folder.CloseAsync(false);
                });
            }
            catch (Exception ex)
            {
                SyncState?.Invoke(group.Key.AccountId, false, AccountSession.Friendly(ex));
            }
        }
    }

    /// <summary>Moves messages to another folder (or permanently deletes when already in Trash and target is Trash).</summary>
    public async Task MoveAsync(IEnumerable<MessageRecord> messages, FolderKind target)
    {
        foreach (var group in messages.GroupBy(m => (m.AccountId, m.Folder)).ToList())
        {
            foreach (var m in group) MailStore.DeleteMessage(m.Id);
            if (!_sessions.TryGetValue(group.Key.AccountId, out var session)) continue;
            var uids = group.Select(m => new UniqueId((uint)m.Uid)).ToList();
            try
            {
                await session.RunAsync(async c =>
                {
                    var folder = await session.GetFolderAsync(c, group.Key.Folder);
                    if (folder == null) return;
                    await folder.OpenAsync(FolderAccess.ReadWrite);
                    if (group.Key.Folder == target && target == FolderKind.Trash)
                        await ExpungeAsync(folder, uids);
                    else
                        await MoveOnServerAsync(c, session, folder, uids, target, CancellationToken.None);
                    await folder.CloseAsync(false);
                });
            }
            catch (Exception ex)
            {
                SyncState?.Invoke(group.Key.AccountId, false, AccountSession.Friendly(ex));
            }
            FolderChanged?.Invoke(group.Key.AccountId, group.Key.Folder);
        }
    }

    private static async Task MoveOnServerAsync(MailKit.Net.Imap.ImapClient client, AccountSession session, IMailFolder source,
        IList<UniqueId> uids, FolderKind target, CancellationToken ct)
    {
        var dest = await session.GetFolderAsync(client, target, ct);
        if (dest == null || dest.FullName == source.FullName)
        {
            if (target == FolderKind.Trash) await ExpungeAsync(source, uids);
            return;
        }
        if (!source.IsOpen || source.Access != FolderAccess.ReadWrite) await source.OpenAsync(FolderAccess.ReadWrite, ct);
        await source.MoveToAsync(uids, dest, ct);
    }

    private static async Task ExpungeAsync(IMailFolder folder, IList<UniqueId> uids)
    {
        await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
        await folder.ExpungeAsync(uids);
    }

    // ---------------- Sending / drafts ----------------
    public async Task SendAsync(long accountId, MimeMessage message)
    {
        await Session(accountId).SendAsync(message);
        MailStore.RecordSent(message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
            .Select(mb => (mb.Address, mb.Name ?? "")));
        SpamEngine.RefreshLists();
        _ = SyncFolderAsync(accountId, FolderKind.Sent);
    }

    public async Task SaveDraftAsync(long accountId, MimeMessage message)
    {
        var session = Session(accountId);
        await session.RunAsync(async c =>
        {
            var drafts = await session.GetFolderAsync(c, FolderKind.Drafts);
            if (drafts != null) await drafts.AppendAsync(message, MessageFlags.Draft | MessageFlags.Seen);
        });
        _ = SyncFolderAsync(accountId, FolderKind.Drafts);
    }

    // ---------------- Spam / blocking ----------------
    public async Task MarkSpamAsync(IEnumerable<MessageRecord> messages)
    {
        var list = messages.ToList();
        foreach (var m in list) SpamEngine.Train(m, true);
        await MoveAsync(list.Where(m => m.Folder != FolderKind.Junk), FolderKind.Junk);
    }

    public async Task NotSpamAsync(IEnumerable<MessageRecord> messages)
    {
        var list = messages.ToList();
        foreach (var m in list)
        {
            SpamEngine.Train(m, false);
            if (m.Folder != FolderKind.Junk)
            {
                m.IsSpam = false;
                MailStore.SetSpam(m.Id, false, "");
            }
        }
        var inJunk = list.Where(m => m.Folder == FolderKind.Junk).ToList();
        if (inJunk.Count > 0) await MoveAsync(inJunk, FolderKind.Inbox);
        foreach (var id in list.Select(m => m.AccountId).Distinct()) FolderChanged?.Invoke(id, FolderKind.Inbox);
    }

    /// <summary>Blocks a sender (address or "@domain") and clears their existing mail out of every inbox.</summary>
    public async Task<int> BlockAsync(string pattern)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        MailStore.AddBlocked(pattern);
        SpamEngine.RefreshLists();
        var existing = pattern.StartsWith('@')
            ? MailStore.MessagesFromDomain(pattern[1..], FolderKind.Inbox)
            : MailStore.MessagesFrom(pattern, FolderKind.Inbox);
        if (existing.Count > 0) await MoveAsync(existing, FolderKind.Trash);
        return existing.Count;
    }
}
