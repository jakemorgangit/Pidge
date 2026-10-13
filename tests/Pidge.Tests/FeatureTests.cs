using System.IO;
using MailKit;
using Pidge.Data;
using Pidge.Models;
using Pidge.Services;
using MailStore = Pidge.Data.MailStore;

namespace Pidge.Tests;

[Collection("GreenMail")]
public class FeatureTests(GreenMailFixture server)
{
    private readonly MailEngine _engine = MailEngine.Instance;

    [Fact]
    public async Task Custom_imap_folders_are_listed_and_their_mail_syncs()
    {
        var email = GreenMailFixture.NewAddress("folders");
        await GreenMailFixture.SendAsync(email, "Invoice 1");
        await GreenMailFixture.SendAsync(email, "Hello");
        var account = server.CreateAccount(email);

        // Another client files a message into Clients/Acme.
        await GreenMailFixture.WithImapAsync(email, async imap =>
        {
            var root = imap.GetFolder(imap.PersonalNamespaces[0]);
            var clients = await root.CreateAsync("Clients", false);
            var acme = await clients.CreateAsync("Acme", true);
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
            var uids = await imap.Inbox.SearchAsync(MailKit.Search.SearchQuery.SubjectContains("Invoice"));
            await imap.Inbox.MoveToAsync(uids, acme);
        });

        await _engine.RefreshFoldersAsync(account.Id);
        var folders = MailStore.GetFolders(account.Id);
        var acmeInfo = Assert.Single(folders, f => f.Name == "Acme");
        Assert.Equal(1, acmeInfo.Depth);
        Assert.DoesNotContain(folders, f => f.Name.Equals("INBOX", StringComparison.OrdinalIgnoreCase));

        await _engine.SyncFolderAsync(account.Id, FolderKind.Custom, acmeInfo.Path);
        var inFolder = MailStore.QueryMessages(account.Id, FolderKind.Custom, MessageFilter.All, null, 100, acmeInfo.Path);
        Assert.Equal("Invoice 1", Assert.Single(inFolder).Subject);

        // ...and the body opens (downloaded, cached compressed, reloaded from cache).
        var mime = await _engine.GetFullMessageAsync(inFolder[0]);
        Assert.Equal("Invoice 1", mime.Subject);
        Assert.True(File.Exists(MessageCache.PathFor(inFolder[0])));
        Assert.EndsWith(".emlz", MessageCache.PathFor(inFolder[0]));
        var again = await MessageCache.TryLoadAsync(inFolder[0]);
        Assert.Equal("Invoice 1", again!.Subject);
    }

    [Fact]
    public async Task Moving_to_a_custom_folder_files_the_message_on_the_server()
    {
        var email = GreenMailFixture.NewAddress("move");
        await GreenMailFixture.SendAsync(email, "File me");
        var account = server.CreateAccount(email);
        await GreenMailFixture.WithImapAsync(email, async imap =>
            await imap.GetFolder(imap.PersonalNamespaces[0]).CreateAsync("Receipts", true));
        await _engine.RefreshFoldersAsync(account.Id);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);

        var msg = Assert.Single(GreenMailFixture.Local(account.Id));
        var receipts = MailStore.GetFolders(account.Id).Single(f => f.Name == "Receipts");
        await _engine.MoveAsync([msg], FolderKind.Custom, receipts.Path);

        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Custom, receipts.Path);
        Assert.Empty(GreenMailFixture.Local(account.Id));
        Assert.Single(MailStore.QueryMessages(account.Id, FolderKind.Custom, MessageFilter.All, null, 10, receipts.Path));
    }

    [Fact]
    public async Task Tagging_learns_the_sender_and_tags_their_new_mail()
    {
        var email = GreenMailFixture.NewAddress("tags");
        await GreenMailFixture.SendAsync(email, "March invoice", from: "billing@supplier-a.test");
        await GreenMailFixture.SendAsync(email, "Old invoice", from: "billing@supplier-a.test");
        await GreenMailFixture.SendAsync(email, "Lunch?", from: "friend@elsewhere.test");
        var account = server.CreateAccount(email);
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);

        var tag = MailStore.CreateTag("Invoices " + Guid.NewGuid().ToString("N")[..4], "#FFB648");
        var march = GreenMailFixture.Local(account.Id).Single(m => m.Subject == "March invoice");
        var auto = TagEngine.Tag([march], tag);
        Assert.Equal(1, auto); // the older invoice from the same sender was tagged too

        await GreenMailFixture.SendAsync(email, "April invoice", from: "billing@supplier-a.test");
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);

        var tagged = MailStore.QueryTagged(tag.Id, MessageFilter.All, null, 100).Select(m => m.Subject).ToList();
        Assert.Contains("April invoice", tagged);
        Assert.DoesNotContain("Lunch?", tagged);

        // A second person from the same organisation widens the rule to the domain.
        await GreenMailFixture.SendAsync(email, "Statement", from: "accounts@supplier-a.test");
        await _engine.SyncFolderAsync(account.Id, FolderKind.Inbox);
        TagEngine.Tag([GreenMailFixture.Local(account.Id).Single(m => m.Subject == "Statement")], tag);
        Assert.Contains(MailStore.GetTagRules(), r => r.TagId == tag.Id && r.Pattern == "@supplier-a.test");

        // Untagging stops automatic tagging for that sender.
        TagEngine.Untag([march], tag);
        Assert.DoesNotContain(MailStore.GetTagRules(), r => r.TagId == tag.Id && r.Pattern == "billing@supplier-a.test");
    }

    [Fact]
    public void Shared_settings_round_trip_adds_and_removes_accounts()
    {
        var a = server.CreateAccount(GreenMailFixture.NewAddress("share-a"));
        var state = ConfigSync.Capture();

        // Another PC removed account A and added B.
        state.Accounts.RemoveAll(x => x.AccountGuid == a.AccountGuid);
        var bEmail = GreenMailFixture.NewAddress("share-b");
        state.Accounts.Add(new ConfigSync.SharedAccount
        {
            AccountGuid = Guid.NewGuid().ToString("N"), Email = bEmail, DisplayName = "B", Username = bEmail, Secret = "pw",
            ImapHost = "imap.example.test", ImapPort = 993, ImapSecurity = 2, SmtpHost = "smtp.example.test", SmtpPort = 465, SmtpSecurity = 2
        });
        state.Templates.Add(new ServerTemplate { Name = "DreamHost", ImapHost = "imap.dreamhost.com", SmtpHost = "smtp.dreamhost.com" });

        ConfigSync.Apply(state, merge: false);

        var accounts = MailStore.GetAccounts();
        Assert.DoesNotContain(accounts, x => x.AccountGuid == a.AccountGuid);
        var b = Assert.Single(accounts, x => x.Email == bEmail);
        Assert.Equal("pw", Secrets.Unprotect(b.ProtectedPassword));
        Assert.Contains(MailStore.GetTemplates(), t => t.Name == "DreamHost");
    }

    [Fact]
    public void Shared_settings_file_is_encrypted_and_needs_the_passphrase()
    {
        var plain = System.Text.Encoding.UTF8.GetBytes("""{"Accounts":[{"Secret":"hunter2"}]}""");
        var cipher = SettingsTransfer.Encrypt(plain, "correct horse battery");
        Assert.DoesNotContain("hunter2", System.Text.Encoding.UTF8.GetString(cipher));
        Assert.Equal(plain, SettingsTransfer.Decrypt(cipher, "correct horse battery"));
        Assert.Throws<InvalidDataException>(() => SettingsTransfer.Decrypt(cipher, "wrong passphrase"));
    }
}
