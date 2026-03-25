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

        /// <summary>Whether STARTTLS is advertised and supported by the built-in IMAP server. Defaults to true (auto-generates a self-signed certificate if no TlsCertificatePath is set).</summary>
        public bool EnableStartTls { get; set; } = true;

        /// <summary>Whether clients must issue STARTTLS before LOGIN/AUTHENTICATE is accepted.</summary>
        public bool RequireStartTls { get; set; } = false;

        /// <summary>Path to the TLS certificate (PFX) used for STARTTLS.</summary>
        public string? TlsCertificatePath { get; set; }

        /// <summary>Password for the TLS certificate (if required).</summary>
        public string? TlsCertificatePassword { get; set; }
    }
}
