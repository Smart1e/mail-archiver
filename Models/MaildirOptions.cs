namespace MailArchiver.Models
{
    /// <summary>
    /// Controls the background service that projects archived emails onto a Maildir tree
    /// for Dovecot to serve. Disabled by default — opt in via the Maildir__Enabled env var.
    /// </summary>
    public class MaildirOptions
    {
        public const string Maildir = "Maildir";

        /// <summary>Whether to run the MaildirExportService. Defaults to false.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Root directory where per-account Maildir trees live. Each account gets a subdirectory
        /// named after its MailAccount.Id (matching the Dovecot passdb return value). Defaults to
        /// /var/mail — the shared volume mounted in both the app and Dovecot containers.
        /// </summary>
        public string Root { get; set; } = "/var/mail";

        /// <summary>How often to scan for new / deleted emails and sync the Maildir tree.</summary>
        public int TickSeconds { get; set; } = 30;

        /// <summary>How many emails to materialize per DB batch — keeps memory bounded on initial import.</summary>
        public int BatchSize { get; set; } = 50;
    }
}
