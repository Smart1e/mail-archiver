namespace MailArchiver.Models
{
    public class ImapServerOptions
    {
        public const string ImapServer = "ImapServer";

        /// <summary>Whether the built-in IMAP server is enabled. Defaults to false.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>TCP port to listen on. Defaults to 143 (standard IMAP).</summary>
        public int Port { get; set; } = 143;

        /// <summary>IP address to bind to. Defaults to all interfaces.</summary>
        public string Host { get; set; } = "0.0.0.0";
    }
}
