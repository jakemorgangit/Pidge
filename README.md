# QuickMail

A sleek, dark-mode email client for Windows, built for people who juggle a lot of email accounts.

- **Account cards**: every account is a small card in the left pane with its own colour and unread count. Click one to open its inbox.
- **All inboxes**: treat every account as one mailbox. Read, reply, triage and search across all of them, and replies go out from the right account automatically.
- **Dashboard** (Ctrl+D, from anywhere): unread per account, mail volume over the last 14 days, what kind of mail is arriving, unread mail from people you know, top senders, and senders worth cleaning up.
- **Easy setup**: type an address and password. QuickMail finds the IMAP/SMTP settings itself (known providers, MX records for custom domains, the Thunderbird autoconfig database, then probing).
- **Spam detection that learns**: server verdicts, SPF/DKIM/DMARC failures, brand impersonation, look-alike links, risky domains and spammy wording, plus a Bayesian filter trained by every *Spam* / *Not spam* you press. Suspected spam is held for review rather than silently deleted.
- **Blocking**: block a sender or a whole domain. Existing mail is binned and anything new skips the inbox in every account.
- **Pattern recognition**: newsletters, notifications, social and promotions are tagged automatically, and noisy senders you never open are flagged on the dashboard.
- **Rich compose**: formatting, links, quotes, signatures per account, attachments (drag and drop), contact autocomplete, drafts.
- **Privacy**: remote images are blocked until you allow them, scripts never run in emails, and passwords are encrypted with Windows DPAPI.
- **Backup & move**: export every account (including passwords), the block list, contacts and learned spam data to a single AES-256 encrypted, password-protected file, and import it on another PC.
- **Auto-update**: QuickMail checks GitHub Releases on startup and can download and install new versions itself.

## Keyboard

| Anywhere | | In the message list | |
|---|---|---|---|
| Ctrl+N | New message | R / A / F | Reply / reply all / forward |
| Ctrl+D | Dashboard | E | Archive |
| Ctrl+0 | All inboxes | Del | Delete |
| Ctrl+1…9 | Jump to account | J | Spam |
| Ctrl+F | Search | S | Flag |
| F5 | Check mail | U | Read / unread |

Compose: **Ctrl+Enter** send · **Ctrl+K** link · **Ctrl+Shift+S** save draft.

## Install

Download the latest `QuickMail-x.y.z-x64.msi` from [Releases](https://github.com/jakemorgangit/QuickMail/releases) and run it. It installs per-user (no admin rights needed) to `%LOCALAPPDATA%\Programs\QuickMail` and includes the .NET runtime. It also needs the Microsoft Edge WebView2 runtime, which comes with Windows 11.

> **Gmail, Outlook.com, Yahoo and iCloud** need an *app password* rather than your normal password when two-step sign-in is on. QuickMail tells you this when you add the account.

## Build

Requires the .NET 10 SDK.

```powershell
dotnet run --project src/QuickMail            # run from source
./build.ps1 -Version 1.0.0                    # publish + build artifacts/QuickMail-1.0.0-x64.msi
```

The MSI is built with the WiX Toolset v5 SDK, restored automatically from NuGet.

## Releasing

```powershell
git tag v1.1.0
git push origin v1.1.0
```

The **Release** workflow builds the MSI and publishes a GitHub release. Installed copies pick it up the next time they start, or straight away from *Settings → Updates*.

## Where data lives

Everything is stored locally in `%LOCALAPPDATA%\QuickMail`: a SQLite cache of message headers, cached message files, settings and the WebView2 profile. Nothing is sent anywhere except your own mail servers, plus a one-off DNS/autoconfig lookup of your domain when you add an account and the GitHub update check.

For UI testing with fake data, set `QUICKMAIL_DATA` to an empty folder, start the app once, then run `python tools/seed_demo.py <folder>`.
