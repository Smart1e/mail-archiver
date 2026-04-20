using MailArchiver.Models;
using System.ComponentModel.DataAnnotations.Schema;

public class MailAccount
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string EmailAddress { get; set; }
    public string? ImapServer { get; set; }
    public int? ImapPort { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSSL { get; set; }
    public DateTime LastSync { get; set; }
    public bool IsEnabled { get; set; } = true;
    
    
    // Folder exclusion functionality
    public string ExcludedFolders { get; set; } = string.Empty;
    
    // Email deletion functionality
    public int? DeleteAfterDays { get; set; }
    
    // Local archive retention functionality
    public int? LocalRetentionDays { get; set; }

    // Minimum age for emails to be archived (null = archive all)
    public int? MinEmailAgeDays { get; set; }

    // Password for built-in IMAP server access (null = IMAP access disabled for this account)
    public string? ImapPassword { get; set; }

    // Username for built-in IMAP server access. When set, clients must log in using this
    // value instead of EmailAddress — lets you expose the archive under a different address
    // (e.g. sales@archive.local) without disturbing EmailAddress / Username used for real
    // upstream sync. The "archive-" prefix-strip still applies, so clients can log in as
    // either "archive-sales@archive.local" or "sales@archive.local". When null, the server
    // matches on EmailAddress (original behaviour).
    public string? ArchiveImapUsername { get; set; }

    // Provider field for account type
    public ProviderType Provider { get; set; } = ProviderType.IMAP;
    
    // Microsoft 365 OAuth2 fields
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? TenantId { get; set; }
    
    [NotMapped]
    public List<string> ExcludedFoldersList
    {
        get
        {
            return string.IsNullOrEmpty(ExcludedFolders) 
                ? new List<string>() 
                : ExcludedFolders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }
    }
    
    public virtual ICollection<ArchivedEmail> ArchivedEmails { get; set; } = new List<ArchivedEmail>();
    
    // Navigation properties for multi-user functionality
    public virtual ICollection<UserMailAccount> UserMailAccounts { get; set; } = new List<UserMailAccount>();
}
