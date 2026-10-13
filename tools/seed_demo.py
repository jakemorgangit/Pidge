"""Seeds a throwaway Pidge data folder with fake accounts and mail for UI testing.

Usage:
    1. set PIDGE_DATA=C:\\temp\\qm-demo and start Pidge once (creates the database), then close it
    2. python tools/seed_demo.py C:\\temp\\qm-demo
    3. start Pidge again with the same PIDGE_DATA, plus PIDGE_OFFLINE=1 so it
       doesn't try to reach the (fake) mail servers
"""
import os
import random
import sqlite3
import sys
import time
from email.message import EmailMessage
from email.utils import format_datetime, make_msgid
from datetime import datetime, timezone

data_dir = sys.argv[1]
db = sqlite3.connect(os.path.join(data_dir, "pidge.db"))
random.seed(7)

accounts = [
    ("Black Cat", "jake@blackcat.example", "#7C6CFF"),
    ("Personal", "jake.personal@example.com", "#4CC9F0"),
    ("Shop", "orders@myshop.example", "#36D399"),
    ("Side project", "hello@sideproject.example", "#FFB648"),
]
db.execute("DELETE FROM Accounts")
db.execute("DELETE FROM Messages")
for i, (name, email, color) in enumerate(accounts):
    db.execute(
        "INSERT INTO Accounts(DisplayName,Email,SenderName,Color,ImapHost,ImapPort,ImapSecurity,SmtpHost,SmtpPort,SmtpSecurity,Username,SortOrder) "
        "VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",
        (name, email, "Jake", color, "127.0.0.1", 9, 0, "127.0.0.1", 9, 0, email, i))
ids = [r[0] for r in db.execute("SELECT Id FROM Accounts ORDER BY SortOrder")]

people = [("Sarah Jones", "sarah@acme.example"), ("Tom Price", "tom.price@clientco.example"), ("Mum", "mum@family.example"),
          ("Priya Patel", "priya@designstudio.example"), ("Dave Evans", "dave@supplier.example")]
bulk = [("The Verge", "newsletter@theverge.example", 1), ("GitHub", "noreply@github.example", 2), ("LinkedIn", "messages@linkedin.example", 3),
        ("Big Sale Store", "deals@bigsale.example", 4), ("Stripe", "notifications@stripe.example", 2)]
spam = [("PayPal Security", "secure-alert@paypa1-verify.xyz", "URGENT: Your account has been suspended!!!", "Verify your account now or it will be closed. Click here to confirm your identity."),
        ("Lottery Board", "winner88213@prizes.top", "CONGRATULATIONS YOU HAVE WON", "Claim your prize of one million dollars. Wire transfer fee required."),
        ("Crypto Gains", "invest@quick-crypto.click", "Guaranteed 400% bitcoin returns", "Risk-free investment opportunity, act now limited time.")]
subjects = ["Invoice for October", "Re: Project timeline", "Quick question about the logo", "Lunch on Friday?", "Contract draft v3",
            "Photos from the weekend", "Re: Website launch", "Can you call me?", "Order #4821 has shipped", "Meeting notes"]

now = int(time.time())
uid = {i: 100 for i in ids}
cache = os.path.join(data_dir, "cache")


def add(acct, name, addr, subject, preview, ts, read, flagged=False, cat=0, is_spam=False, score=0.0, reasons="", unsub="", attach=False):
    uid[acct] += 1
    u = uid[acct]
    db.execute(
        "INSERT INTO Messages(AccountId,Folder,Uid,UidValidity,MessageId,Subject,FromName,FromAddress,ToList,DateUnix,Preview,IsRead,IsFlagged,"
        "HasAttachments,IsSpam,SpamScore,SpamReasons,Category,ListUnsubscribe) VALUES(?,0,?,1,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (acct, u, f"{u}.{acct}@demo", subject, name, addr, accounts[ids.index(acct)][1], ts, preview, int(read), int(flagged),
         int(attach), int(is_spam), score, reasons, cat, unsub))
    msg = EmailMessage()
    msg["From"] = f"{name} <{addr}>"
    msg["To"] = accounts[ids.index(acct)][1]
    msg["Subject"] = subject
    msg["Date"] = format_datetime(datetime.fromtimestamp(ts, timezone.utc))
    msg["Message-ID"] = make_msgid()
    msg.set_content(preview)
    msg.add_alternative(f"<html><body><p>Hi Jake,</p><p>{preview}</p><p>Thanks,<br>{name}</p></body></html>", subtype="html")
    if attach:
        msg.add_attachment(b"%PDF-1.4 demo", maintype="application", subtype="pdf", filename="document.pdf")
    path = os.path.join(cache, str(acct), "Inbox")
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, f"1_{u}.eml"), "wb") as f:
        f.write(bytes(msg))


for day in range(14):
    for _ in range(random.randint(3, 9)):
        acct = random.choice(ids)
        ts = now - day * 86400 - random.randint(0, 80000)
        r = random.random()
        if r < 0.45:
            name, addr = random.choice(people)
            add(acct, name, addr, random.choice(subjects), "Just following up on this. Let me know what you think when you get a chance.",
                ts, read=day > 1 or random.random() < 0.4, flagged=random.random() < 0.12, attach=random.random() < 0.2)
        else:
            name, addr, cat = random.choice(bulk)
            add(acct, name, addr, f"{name}: your weekly update", "Here's what happened this week. Unsubscribe at any time.", ts,
                read=random.random() < (0.05 if cat == 4 else 0.5), cat=cat, unsub="<https://example.com/unsub>")

for name, addr, subj, prev in spam:
    add(random.choice(ids), name, addr, subj, prev, now - random.randint(0, 200000), read=False, is_spam=True, score=0.93,
        reasons="Claims to be paypal but sent from paypa1-verify.xyz\nSpam-like wording\nSender uses a high-risk domain extension")

for name, addr in people[:4]:
    db.execute("INSERT OR REPLACE INTO Contacts(Address,Name,SentCount,ReceivedCount,LastSeenUnix) VALUES(?,?,?,?,?)", (addr, name, 3, 10, now))
db.execute("INSERT OR REPLACE INTO Counters(Key,Value) VALUES('spamCaught',37),('blocked',112)")

# Folders on the first account, and a few tags (with learned sender rules) for the screenshots.
db.execute("DELETE FROM Folders")
for path, name, depth, unread in [("Clients", "Clients", 0, 0), ("Clients/Acme", "Acme", 1, 2), ("Clients/Globex", "Globex", 1, 0),
                                  ("Receipts", "Receipts", 0, 5), ("Projects", "Projects", 0, 1)]:
    db.execute("INSERT INTO Folders(AccountId,Path,Name,Depth,Unread,Total,Selectable,SortKey) VALUES(?,?,?,?,?,?,1,?)",
               (ids[0], path, name, depth, unread, unread + 7, path.lower()))
db.execute("DELETE FROM Tags"); db.execute("DELETE FROM MessageTags"); db.execute("DELETE FROM TagRules")
tags = {"Clients": "#36D399", "Invoices": "#FFB648", "Family": "#F472B6"}
tag_ids = {}
for name, color in tags.items():
    db.execute("INSERT INTO Tags(TagGuid,Name,Color) VALUES(lower(hex(randomblob(16))),?,?)", (name, color))
    tag_ids[name] = db.execute("SELECT last_insert_rowid()").fetchone()[0]
for sender, tag in [("tom.price@clientco.example", "Clients"), ("priya@designstudio.example", "Clients"),
                    ("mum@family.example", "Family"), ("notifications@stripe.example", "Invoices")]:
    db.execute("INSERT INTO TagRules(TagId,Pattern,CreatedUnix) VALUES(?,?,?)", (tag_ids[tag], sender, now))
    for acct, key in db.execute("SELECT AccountId, MessageId FROM Messages WHERE FromAddress=?", (sender,)).fetchall():
        db.execute("INSERT OR IGNORE INTO MessageTags(AccountId,MessageKey,TagId) VALUES(?,?,?)", (acct, key, tag_ids[tag]))
db.commit()
print("seeded", sum(uid.values()) - 100 * len(ids), "messages")
