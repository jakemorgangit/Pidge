using System.Collections.Concurrent;
using MailKit;
using MailKit.Search;
using MimeKit;
using Pidge.Models;
using MailStore = Pidge.Data.MailStore;

namespace Pidge.Services;

/// <summary>
/// Owns the per-account sessions, keeps the local cache in sync with the servers and performs
/// every server-side action (flags, moves, deletes, sending). Events fire on background threads.
/// Folders are addressed by <see cref="FolderKind"/>, plus a server path for <see cref="FolderKind.Custom"/> folders.
/// </summary>
public sealed class MailEngine
{
    public static MailEngine Instance { get; } = new();

    /// <summary>Set PIDGE_OFFLINE=1 to never contact mail servers (demos and UI testing).</summary>
    public static bool Offline { get; } = Environment.GetEnvironmentVariable("PIDGE_OFFLINE") == "1";

    // Two connections per account: one for what you do (open, delete, move, flag) and one for background work
    // (sync, folder listing, downloading new mail), so your actions never queue behind a sync.
    private readonly ConcurrentDictionary<long, AccountSession> _sessions = new();
    private readonly ConcurrentDictionary<long, AccountSession> _background = new();
    private readonly ConcurrentDictionary<long, MailAccount> _accounts = new();
    private readonly ConcurrentDictionary<long, DateTime> _foldersRefreshed = new();
    private CancellationTokenSource? _loopCts;

    /// <summary>Local data for an account/folder changed (account, kind, custom folder path).</summary>
    public event Action<long, FolderKind, string>? FolderChanged;
    /// <summary>Account sync started/stopped; error is null on success.</summary>
    public event Action<long, bool, string?>? SyncState;
    /// <summary>New, unread, non-spam mail arrived in an inbox (account, the new messages).</summary>
    public event Action<long, IReadOnlyList<MessageRecord>>? NewMail;
    /// <summary>An account's folder list was refreshed from the server.</summary>
    public event Action<long>? FoldersChanged;

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
        if (_background.TryGetValue(account.Id, out var b)) b.UpdateAccount(account);
        else _background[account.Id] = new AccountSession(account);
    }

    public void Unregister(long accountId)
    {
        _accounts.TryRemove(accountId, out _);
        if (_sessions.TryRemove(accountId, out var s)) s.Dispose();
        if (_background.TryRemove(accountId, out var b)) b.Dispose();
    }

    public MailAccount? Account(long id) => _accounts.TryGetValue(id, out var a) ? a : null;
    private AccountSession Session(long id) => _sessions[id];
    private AccountSession Background(long id) => _background[id];

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
        Task.WhenAll(_accounts.Values.ToList().Select(a => SyncFolderAsync(a.Id, kind, "", ct)));

    // ---------------- Sync ----------------
    public async Task<int> SyncFolderAsync(long accountId, FolderKind kind, string path = "", CancellationToken ct = default)
    {
        if (Offline || !_accounts.TryGetValue(accountId, out var account)) return 0;
        SyncState?.Invoke(accountId, true, null);
        try
        {
            var (fresh, inserted) = await Background(accountId).RunAsync(c => SyncCoreAsync(c, account, kind, path, ct), ct);
            if (kind == FolderKind.Inbox) MailStore.SetLastSync(accountId);
            SyncState?.Invoke(accountId, false, null);
            FolderChanged?.Invoke(accountId, kind, path);
            if (fresh.Count > 0 && kind == FolderKind.Inbox) NewMail?.Invoke(accountId, fresh);

            // Download new mail in the background so it opens instantly.
            if (inserted.Count > 0) _ = PrefetchAsync(inserted, ct);

            // Keep the folder list fresh without listing on every sync.
            if (kind == FolderKind.Inbox && DateTime.UtcNow - _foldersRefreshed.GetValueOrDefault(accountId) > TimeSpan.FromMinutes(15))
                _ = RefreshFoldersAsync(accountId, ct);
            return fresh.Count;
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

    private async Task<(List<MessageRecord> fresh, List<MessageRecord> inserted)> SyncCoreAsync(MailKit.Net.Imap.ImapClient client, MailAccount account,
        FolderKind kind, string path, CancellationToken ct)
    {
        var session = Background(account.Id);
        var folder = await session.GetFolderAsync(client, kind, path, ct);
        if (folder == null) return ([], []);

        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        long validity = folder.UidValidity;
        var localValidity = MailStore.GetUidValidity(account.Id, kind, path);
        if (localValidity != 0 && localValidity != validity) MailStore.ClearFolder(account.Id, kind, path);

        long maxUid = MailStore.GetMaxUid(account.Id, kind, path);
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

        var (records, toTrash, toJunk) = Process(account, kind, path, validity, summaries);
        if (records.Count > 0)
        {
            MailStore.InsertMessages(records);
            if (kind == FolderKind.Inbox) MailStore.RecordReceived(records);
            if (kind is not (FolderKind.Junk or FolderKind.Trash)) TagEngine.ApplyRules(records);
        }

        if (toTrash.Count > 0) await MoveOnServerAsync(client, session, folder, toTrash, FolderKind.Trash, "", ct);
        if (toJunk.Count > 0) await MoveOnServerAsync(client, session, folder, toJunk, FolderKind.Junk, "", ct);

        if (maxUid > 0) await SyncFlagsAsync(folder, account.Id, kind, path, maxUid, ct);
        await folder.CloseAsync(false, ct);

        if (kind == FolderKind.Custom)
        {
            var (unread, total) = MailStore.CountFolder(account.Id, kind, path);
            MailStore.SetFolderUnread(account.Id, path, unread, Math.Max(total, folder.Count));
        }

        // Pre-download: everything new, or the most recent messages on a folder's first sync.
        var inserted = maxUid == 0 ? records.OrderByDescending(r => r.DateUnix).Take(PrefetchOnFirstSync).ToList() : records;

        // The first sync of a folder brings in history, which isn't "new mail".
        if (maxUid == 0) return ([], inserted);
        // Re-read so records carry their database ids (needed to open them from a notification).
        var fresh = records.Where(r => !r.IsRead && !r.IsSpam).ToList();
        return (fresh.Count == 0 ? [] : MailStore.QueryMessages(account.Id, kind, Pidge.Data.MessageFilter.All, null, 50, path)
            .Where(m => fresh.Any(f => f.Uid == m.Uid)).ToList(), inserted);
    }

    /// <summary>Builds records and applies the block list and spam filter to newly fetched mail.</summary>
    private static (List<MessageRecord> records, List<UniqueId> toTrash, List<UniqueId> toJunk) Process(
        MailAccount account, FolderKind kind, string path, long validity, IEnumerable<IMessageSummary> summaries)
    {
        var records = new List<MessageRecord>();
        var toTrash = new List<UniqueId>();
        var toJunk = new List<UniqueId>();

        foreach (var s in summaries)
        {
            var rec = Build(account.Id, kind, path, validity, s);
            rec.Category = SpamEngine.Categorize(rec, s.Headers);
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
            records.Add(rec);
        }
        return (records, toTrash, toJunk);
    }

    private static MessageRecord Build(long accountId, FolderKind kind, string path, long validity, IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From?.Mailboxes.FirstOrDefault();
        var flags = s.Flags ?? MessageFlags.None;
        var date = env?.Date ?? s.InternalDate ?? DateTimeOffset.Now;
        return new MessageRecord
        {
            AccountId = accountId,
            Folder = kind,
            FolderPath = path,
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

    private static async Task SyncFlagsAsync(IMailFolder folder, long accountId, FolderKind kind, string path, long maxUid, CancellationToken ct)
    {
        long minUid = MailStore.GetMinUid(accountId, kind, path);
        if (minUid == 0) return;
        // Only compare the range we ask the server about; messages inserted moments ago have higher UIDs
        // and must not be mistaken for messages deleted on the server.
        var local = MailStore.GetLocalFlags(accountId, kind, path, minUid, maxUid);
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
        if (changes.Count > 0 || vanished.Count > 0) MailStore.ApplyFlagChanges(accountId, kind, path, changes, vanished);
    }

    /// <summary>Fetches older messages than the oldest one cached.</summary>
    public async Task<int> LoadOlderAsync(long accountId, FolderKind kind, string path = "", int count = 200)
    {
        if (Offline || !_accounts.TryGetValue(accountId, out var account)) return 0;
        var session = Background(accountId);
        var added = await session.RunAsync(async c =>
        {
            var folder = await session.GetFolderAsync(c, kind, path);
            if (folder == null) return 0;
            await folder.OpenAsync(FolderAccess.ReadOnly);
            long minUid = MailStore.GetMinUid(accountId, kind, path);
            if (minUid <= 1) return 0;
            var uids = await folder.SearchAsync(SearchQuery.Uids(new UniqueIdRange(UniqueId.MinValue, new UniqueId((uint)minUid - 1))));
            var page = uids.OrderByDescending(u => u.Id).Take(count).ToList();
            if (page.Count == 0) return 0;
            var summaries = await folder.FetchAsync(page, SummaryRequest);
            var (records, _, _) = Process(account, kind, path, folder.UidValidity, summaries);
            MailStore.InsertMessages(records);
            TagEngine.ApplyRules(records);
            await folder.CloseAsync(false);
            return records.Count;
        });
        FolderChanged?.Invoke(accountId, kind, path);
        return added;
    }

    // ---------------- Folders ----------------
    /// <summary>Lists the account's own IMAP folders (everything except Inbox and the special folders).</summary>
    public async Task RefreshFoldersAsync(long accountId, CancellationToken ct = default)
    {
        if (Offline || !_accounts.TryGetValue(accountId, out _)) return;
        _foldersRefreshed[accountId] = DateTime.UtcNow;
        var session = Background(accountId);
        try
        {
            var folders = await session.RunAsync(async c =>
            {
                if (c.PersonalNamespaces.Count == 0) return new List<FolderInfo>();
                var special = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.Inbox.FullName };
                foreach (var k in new[] { FolderKind.Sent, FolderKind.Drafts, FolderKind.Archive, FolderKind.Junk, FolderKind.Trash })
                    if (await session.GetFolderAsync(c, k, "", ct) is { } f) special.Add(f.FullName);

                var ns = c.PersonalNamespaces[0];
                IList<IMailFolder> all;
                try { all = await c.GetFoldersAsync(ns, StatusItems.Unread | StatusItems.Count, false, ct); }
                catch (MailKit.Net.Imap.ImapCommandException) { all = await c.GetFoldersAsync(ns, false, ct); }

                const FolderAttributes systemFolders = FolderAttributes.All | FolderAttributes.Important | FolderAttributes.Flagged |
                                                       FolderAttributes.Sent | FolderAttributes.Drafts | FolderAttributes.Junk |
                                                       FolderAttributes.Trash | FolderAttributes.Archive | FolderAttributes.NonExistent;
                var inboxPrefix = c.Inbox.FullName + c.Inbox.DirectorySeparator;
                var result = new List<FolderInfo>();
                foreach (var f in all)
                {
                    if (special.Contains(f.FullName) || (f.Attributes & systemFolders) != 0) continue;
                    var relative = f.FullName.StartsWith(inboxPrefix, StringComparison.OrdinalIgnoreCase) ? f.FullName[inboxPrefix.Length..] : f.FullName;
                    var depth = f.DirectorySeparator == '\0' ? 0 : relative.Count(ch => ch == f.DirectorySeparator);
                    result.Add(new FolderInfo
                    {
                        AccountId = accountId,
                        Path = f.FullName,
                        Name = f.Name,
                        Depth = depth,
                        Unread = Math.Max(0, f.Unread),
                        Total = Math.Max(0, f.Count),
                        Selectable = !f.Attributes.HasFlag(FolderAttributes.NoSelect),
                        SortKey = relative.ToLowerInvariant()
                    });
                }
                // Hide containers that have nothing selectable under them (e.g. an empty "[Gmail]").
                return result.Where(r => r.Selectable || result.Any(o => o.Selectable && o.Path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase) && o.Path != r.Path)).ToList();
            }, ct);
            MailStore.ReplaceFolders(accountId, folders);
            FoldersChanged?.Invoke(accountId);
        }
        catch (Exception ex)
        {
            SyncState?.Invoke(accountId, false, AccountSession.Friendly(ex));
        }
    }

    // ---------------- Message actions ----------------
    public async Task<MimeMessage> GetFullMessageAsync(MessageRecord m)
    {
        if (await MessageCache.TryLoadAsync(m) is { } cached) return cached;

        var session = Session(m.AccountId);
        var message = await session.RunAsync(async c =>
        {
            var folder = await session.GetFolderAsync(c, m.Folder, m.FolderPath) ?? throw new InvalidOperationException("Folder not found on server.");
            await folder.OpenAsync(FolderAccess.ReadOnly);
            var msg = await folder.GetMessageAsync(new UniqueId((uint)m.Uid));
            await folder.CloseAsync(false);
            return msg;
        });

        await MessageCache.SaveAsync(m, message);
        return message;
    }

    public Task SetReadAsync(IEnumerable<MessageRecord> messages, bool read)
    {
        var list = messages.ToList();
        foreach (var m in list) { m.IsRead = read; MailStore.SetRead(m.Id, read); }
        return StoreFlagAsync(list, MessageFlags.Seen, read);
    }

    public Task SetFlaggedAsync(IEnumerable<MessageRecord> messages, bool flagged)
    {
        var list = messages.ToList();
        foreach (var m in list) { m.IsFlagged = flagged; MailStore.SetFlagged(m.Id, flagged); }
        return StoreFlagAsync(list, MessageFlags.Flagged, flagged);
    }

    public Task SetAnsweredAsync(MessageRecord m)
    {
        m.IsAnswered = true;
        MailStore.SetAnswered(m.Id);
        return StoreFlagAsync([m], MessageFlags.Answered, true);
    }

    private async Task StoreFlagAsync(IEnumerable<MessageRecord> messages, MessageFlags flag, bool add)
    {
        if (Offline) return;
        foreach (var group in messages.GroupBy(m => (m.AccountId, m.Folder, m.FolderPath)).ToList())
        {
            if (!_sessions.TryGetValue(group.Key.AccountId, out var session)) continue;
            var uids = group.Select(m => new UniqueId((uint)m.Uid)).ToList();
            try
            {
                await session.RunAsync(async c =>
                {
                    var folder = await session.GetFolderAsync(c, group.Key.Folder, group.Key.FolderPath);
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

    /// <summary>
    /// Moves messages to another folder, or permanently deletes them when they are already in the bin and the target is the bin.
    /// The local copy is only removed once the server has confirmed, so a failure never makes mail vanish from view.
    /// Returns false if any part failed.
    /// </summary>
    public async Task<bool> MoveAsync(IEnumerable<MessageRecord> messages, FolderKind target, string targetPath = "")
    {
        bool ok = true;
        foreach (var group in messages.GroupBy(m => (m.AccountId, m.Folder, m.FolderPath)).ToList())
        {
            if (Offline) await Task.Delay(1200); // demo mode: behave like a real server round trip
            if (!Offline && _sessions.TryGetValue(group.Key.AccountId, out var session))
            {
                var uids = group.Select(m => new UniqueId((uint)m.Uid)).ToList();
                try
                {
                    await session.RunAsync(async c =>
                    {
                        var folder = await session.GetFolderAsync(c, group.Key.Folder, group.Key.FolderPath)
                                     ?? throw new InvalidOperationException("Folder not found on server.");
                        await folder.OpenAsync(FolderAccess.ReadWrite);
                        if (group.Key.Folder == target && target == FolderKind.Trash)
                            await ExpungeAsync(folder, uids);
                        else
                            await MoveOnServerAsync(c, session, folder, uids, target, targetPath, CancellationToken.None);
                        await folder.CloseAsync(false);
                    });
                }
                catch (Exception ex)
                {
                    ok = false;
                    SyncState?.Invoke(group.Key.AccountId, false, AccountSession.Friendly(ex));
                    continue; // leave the local copy: it's still on the server
                }
            }
            foreach (var m in group)
            {
                MailStore.DeleteMessage(m.Id);
                MessageCache.Delete(m);
            }
            FolderChanged?.Invoke(group.Key.AccountId, group.Key.Folder, group.Key.FolderPath);
        }
        return ok;
    }

    // ---------------- Pre-downloading ----------------
    private const int PrefetchOnFirstSync = 25;
    private const long MaxPrefetchBytes = 15 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, byte> _prefetching = new();

    /// <summary>
    /// Downloads full messages in the background (on the background connection) and stores them compressed,
    /// so opening them later is instant. Already-downloaded and very large messages are skipped.
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<MessageRecord> messages, CancellationToken ct = default)
    {
        if (Offline) return;
        var wanted = messages.Where(m => m.Size <= MaxPrefetchBytes && !MessageCache.IsCached(m))
            .Where(m => _prefetching.TryAdd(MessageCache.PathFor(m), 0)).ToList();
        if (wanted.Count == 0) return;
        try
        {
            foreach (var group in wanted.GroupBy(m => (m.AccountId, m.Folder, m.FolderPath)))
            {
                if (!_background.TryGetValue(group.Key.AccountId, out var session)) continue;
                try
                {
                    await session.RunAsync(async c =>
                    {
                        var folder = await session.GetFolderAsync(c, group.Key.Folder, group.Key.FolderPath, ct);
                        if (folder == null) return;
                        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                        foreach (var m in group.OrderByDescending(m => m.DateUnix))
                        {
                            if (ct.IsCancellationRequested || MessageCache.IsCached(m)) continue;
                            try
                            {
                                var mime = await folder.GetMessageAsync(new UniqueId((uint)m.Uid), ct);
                                await MessageCache.SaveAsync(m, mime);
                            }
                            catch (MessageNotFoundException) { /* moved or deleted meanwhile */ }
                        }
                        await folder.CloseAsync(false, ct);
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error("Background download failed", ex); // the message just downloads when opened instead
                }
            }
        }
        finally
        {
            foreach (var m in wanted) _prefetching.TryRemove(MessageCache.PathFor(m), out _);
        }
    }

    private static async Task MoveOnServerAsync(MailKit.Net.Imap.ImapClient client, AccountSession session, IMailFolder source,
        IList<UniqueId> uids, FolderKind target, string targetPath, CancellationToken ct)
    {
        var dest = await session.GetFolderAsync(client, target, targetPath, ct);
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
    public async Task<bool> MarkSpamAsync(List<MessageRecord> messages)
    {
        foreach (var m in messages) SpamEngine.Train(m, true);
        return await MoveAsync(messages.Where(m => m.Folder != FolderKind.Junk), FolderKind.Junk);
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
        foreach (var id in list.Select(m => m.AccountId).Distinct()) FolderChanged?.Invoke(id, FolderKind.Inbox, "");
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
