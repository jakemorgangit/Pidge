namespace QuickMail.Models;

/// <summary>Logical folder. <see cref="Custom"/> is any other IMAP folder, identified by its path.</summary>
public enum FolderKind { Inbox = 0, Sent = 1, Drafts = 2, Archive = 3, Junk = 4, Trash = 5, Custom = 6 }

public enum AuthMethod { Password = 0, MicrosoftOAuth = 1 }

public enum MailCategory { Personal = 0, Newsletter = 1, Notification = 2, Social = 3, Promotion = 4 }

public class MailAccount
{
    public long Id { get; set; }
    /// <summary>Stable identity used when settings are shared between PCs.</summary>
    public string AccountGuid { get; set; } = Guid.NewGuid().ToString("N");
    public AuthMethod AuthMethod { get; set; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Color { get; set; } = "#7C6CFF";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    /// <summary>MailKit SecureSocketOptions value.</summary>
    public int ImapSecurity { get; set; } = 2;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public int SmtpSecurity { get; set; } = 2;
    public string Username { get; set; } = "";
    /// <summary>DPAPI-protected password, or OAuth refresh token for <see cref="AuthMethod.MicrosoftOAuth"/>.</summary>
    public string ProtectedPassword { get; set; } = "";
    public string Signature { get; set; } = "";
    public int SortOrder { get; set; }
    public long LastSyncUnix { get; set; }

    public MailAccount Clone() => (MailAccount)MemberwiseClone();
}

public class MessageRecord
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public FolderKind Folder { get; set; }
    /// <summary>Server folder path for <see cref="FolderKind.Custom"/> folders; empty otherwise.</summary>
    public string FolderPath { get; set; } = "";
    public long Uid { get; set; }
    public long UidValidity { get; set; }
    public string MessageId { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public string Refs { get; set; } = "";
    public string Subject { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    public string ToList { get; set; } = "";
    public string CcList { get; set; } = "";
    public long DateUnix { get; set; }
    public string Preview { get; set; } = "";
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsAnswered { get; set; }
    public bool HasAttachments { get; set; }
    public bool IsSpam { get; set; }
    public double SpamScore { get; set; }
    public string SpamReasons { get; set; } = "";
    public MailCategory Category { get; set; }
    public string ListUnsubscribe { get; set; } = "";
    public long Size { get; set; }

    public DateTime LocalDate => DateTimeOffset.FromUnixTimeSeconds(DateUnix).LocalDateTime;
    /// <summary>Identifies the message across folder moves (used for tags).</summary>
    public string MessageKey => string.IsNullOrEmpty(MessageId) ? $"uid:{(int)Folder}:{FolderPath}:{Uid}" : MessageId;
    public string FromDomain => FromAddress.Contains('@') ? FromAddress[(FromAddress.LastIndexOf('@') + 1)..].ToLowerInvariant() : "";
}

public class ContactRecord
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public int SentCount { get; set; }
    public int ReceivedCount { get; set; }
    public long LastSeenUnix { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

public class AttachmentInfo
{
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public int Index { get; set; }
    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        _ => $"{Size / 1024.0 / 1024.0:0.0} MB"
    };
}

public class FolderInfo
{
    public long AccountId { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public int Depth { get; set; }
    public int Unread { get; set; }
    public int Total { get; set; }
    public bool Selectable { get; set; } = true;
    public string SortKey { get; set; } = "";
}

public class TagInfo
{
    public long Id { get; set; }
    public string TagGuid { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C6CFF";
    public int Count { get; set; }
}

public class TagRule
{
    public long TagId { get; set; }
    /// <summary>"someone@example.com" or "@example.com".</summary>
    public string Pattern { get; set; } = "";
    public long CreatedUnix { get; set; }
}

public class ServerTemplate
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public int ImapSecurity { get; set; } = 2;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public int SmtpSecurity { get; set; } = 2;
    public override string ToString() => Name;
}