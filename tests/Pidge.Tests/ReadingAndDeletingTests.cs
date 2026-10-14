using Pidge.Models;
using Pidge.Services;
using MailStore = Pidge.Data.MailStore;

namespace Pidge.Tests;

[Collection("GreenMail")]
public class ReadingAndDeletingTests(GreenMailFixture server)
{
    private readonly MailEngine _engine = MailEngine.Instance;

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 10)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [Fact]
    public async Task New_mail_is_downloaded_in_the_background_so_it_opens_instantly()
    {
        var email = GreenMailFixture.NewAddress("prefetch");
        await GreenMailFixture.SendAsync(email, "Already here");
        var account = server.CreateAccount(email);

        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        var first = Assert.Single(GreenMailFixture.Local(account.Id));
        Assert.True(await WaitFor(() => MessageCache.IsCached(first)), "recent mail should be pre-downloaded on the first sync");

        await GreenMailFixture.SendAsync(email, "Just arrived", body: "Read me without waiting");
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        var fresh = GreenMailFixture.Local(account.Id).Single(m => m.Subject == "Just arrived");
        Assert.True(await WaitFor(() => MessageCache.IsCached(fresh)), "new mail should be pre-downloaded");

        var cached = await MessageCache.TryLoadAsync(fresh);
        Assert.Contains("Read me without waiting", cached!.TextBody);
    }

    [Fact]
    public async Task A_delete_the_server_refuses_keeps_the_message()
    {
        var email = GreenMailFixture.NewAddress("keep");
        await GreenMailFixture.SendAsync(email, "Don't lose me");
        await GreenMailFixture.WithImapAsync(email, async imap =>
            await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync("Trash", true));
        var account = server.CreateAccount(email);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        var msg = Assert.Single(GreenMailFixture.Local(account.Id));

        // Point at a folder that doesn't exist on the server so the move fails.
        var broken = new MessageRecord { Id = msg.Id, AccountId = msg.AccountId, Folder = FolderKind.Custom, FolderPath = "Does/Not/Exist", Uid = msg.Uid };
        Assert.False(await _engine.MoveAsync([broken], FolderKind.Trash));
        Assert.NotNull(MailStore.GetMessage(msg.Id));

        // A normal delete succeeds and only then removes it locally.
        Assert.True(await _engine.MoveAsync([msg], FolderKind.Trash));
        Assert.Null(MailStore.GetMessage(msg.Id));
        await _engine.SyncFolderAsync(account.Id, FolderKind.Trash);
        Assert.Single(GreenMailFixture.Local(account.Id, FolderKind.Trash));
    }
}
