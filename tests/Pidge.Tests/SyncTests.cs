using MailKit;
using Pidge.Models;
using Pidge.Services;

namespace Pidge.Tests;

[Collection("GreenMail")]
public class SyncTests(GreenMailFixture server)
{
    private readonly MailEngine _engine = MailEngine.Instance;

    [Fact]
    public async Task New_mail_arriving_after_the_first_sync_shows_up()
    {
        var email = GreenMailFixture.NewAddress("sync");
        await GreenMailFixture.SendAsync(email, "First");
        await GreenMailFixture.SendAsync(email, "Second");
        var account = server.CreateAccount(email);

        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        Assert.Equal(2, GreenMailFixture.Local(account.Id).Count);

        await GreenMailFixture.SendAsync(email, "Third");
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        Assert.Contains(GreenMailFixture.Local(account.Id), m => m.Subject == "Third");

        // A further sync must keep it (this is where new mail used to vanish).
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        Assert.Equal(3, GreenMailFixture.Local(account.Id).Count);

        await GreenMailFixture.SendAsync(email, "Fourth");
        await GreenMailFixture.SendAsync(email, "Fifth");
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        Assert.Equal(5, GreenMailFixture.Local(account.Id).Count);
    }

    [Fact]
    public async Task Changes_made_in_another_client_are_picked_up()
    {
        var email = GreenMailFixture.NewAddress("flags");
        await GreenMailFixture.SendAsync(email, "Keep me");
        await GreenMailFixture.SendAsync(email, "Delete me");
        var account = server.CreateAccount(email);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        Assert.All(GreenMailFixture.Local(account.Id), m => Assert.False(m.IsRead));

        await GreenMailFixture.WithImapAsync(email, async imap =>
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
            var uids = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.All);
            await imap.Inbox.StoreAsync(uids[0], new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true });
            await imap.Inbox.StoreAsync(uids[1], new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
            await imap.Inbox.ExpungeAsync();
        });

        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        var local = GreenMailFixture.Local(account.Id);
        var remaining = Assert.Single(local);
        Assert.Equal("Keep me", remaining.Subject);
        Assert.True(remaining.IsRead);
    }

    [Fact]
    public async Task Marking_read_in_Pidge_updates_the_server()
    {
        var email = GreenMailFixture.NewAddress("read");
        await GreenMailFixture.SendAsync(email, "Read me");
        var account = server.CreateAccount(email);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);

        var msg = Assert.Single(GreenMailFixture.Local(account.Id));
        await _engine.SetReadAsync([msg], true);

        await GreenMailFixture.WithImapAsync(email, async imap =>
        {
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
            var summary = (await imap.Inbox.FetchAsync(0, -1, MessageSummaryItems.Flags)).Single();
            Assert.True(summary.Flags!.Value.HasFlag(MessageFlags.Seen));
        });
    }
}
