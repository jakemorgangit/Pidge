using Pidge.Models;
using Pidge.Services;

namespace Pidge.Tests;

[Collection("GreenMail")]
public class NotificationTests(GreenMailFixture server)
{
    [Fact]
    public async Task New_mail_event_carries_only_genuinely_new_messages_with_ids()
    {
        var email = GreenMailFixture.NewAddress("notify");
        await GreenMailFixture.SendAsync(email, "Old news");
        var account = server.CreateAccount(email);

        var received = new List<MessageRecord>();
        void Handler(long id, IReadOnlyList<MessageRecord> fresh) { if (id == account.Id) received.AddRange(fresh); }
        MailEngine.Instance.NewMail += Handler;
        try
        {
            await MailEngine.Instance.SyncFolderAsync(account.Id, FolderKind.Inbox);
            Assert.Empty(received); // first sync is history, not "new mail"

            await GreenMailFixture.SendAsync(email, "Fresh one");
            await MailEngine.Instance.SyncFolderAsync(account.Id, FolderKind.Inbox);
            var msg = Assert.Single(received);
            Assert.Equal("Fresh one", msg.Subject);
            Assert.True(msg.Id > 0);
        }
        finally
        {
            MailEngine.Instance.NewMail -= Handler;
        }
    }

    [Fact]
    public void Mute_pause_and_people_only_settings_are_respected()
    {
        var s = AppSettings.Current;
        var account = new MailAccount { AccountGuid = Guid.NewGuid().ToString("N"), Email = "me@test" };
        var person = new MessageRecord { Id = 1, FromAddress = "friend@example.org", Category = MailCategory.Personal };
        var newsletter = new MessageRecord { Id = 2, FromAddress = "news@shop.example", Category = MailCategory.Newsletter };
        var spam = new MessageRecord { Id = 3, FromAddress = "x@spam.example", IsSpam = true };
        MessageRecord[] all = [person, newsletter, spam];
        var saved = (s.NotificationsEnabled, s.NotifyPeopleOnly, s.NotificationsPausedUntilUtc, s.MutedAccounts.ToList());
        try
        {
            s.NotificationsEnabled = true;
            s.NotifyPeopleOnly = false;
            s.NotificationsPausedUntilUtc = null;
            s.MutedAccounts.Clear();
            Assert.Equal([1L, 2L], Notifier.Filter(account, all).Select(m => m.Id).OrderBy(i => i));

            s.NotifyPeopleOnly = true;
            Assert.Equal([1L], Notifier.Filter(account, all).Select(m => m.Id));

            Notifier.SetAccountMuted(account, true);
            Assert.Empty(Notifier.Filter(account, all));
            Notifier.SetAccountMuted(account, false);

            Notifier.PauseUntil(DateTime.UtcNow.AddHours(1));
            Assert.Empty(Notifier.Filter(account, all));
            Notifier.PauseUntil(DateTime.UtcNow.AddMinutes(-1)); // pause expired
            Assert.Single(Notifier.Filter(account, all));
        }
        finally
        {
            (s.NotificationsEnabled, s.NotifyPeopleOnly, s.NotificationsPausedUntilUtc, var muted) = saved;
            s.MutedAccounts.Clear();
            s.MutedAccounts.AddRange(muted);
        }
    }
}
