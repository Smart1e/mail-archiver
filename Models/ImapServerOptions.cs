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

        /// <summary>Whether the built-in fake SMTP server is enabled. Defaults to true when IMAP is enabled.</summary>
        public bool SmtpEnabled { get; set; } = true;

        /// <summary>TCP port for the fake SMTP server. Defaults to 25.</summary>
        public int SmtpPort { get; set; } = 25;

        /// <summary>TCP port for implicit-TLS IMAP (IMAPS). Defaults to 993. Set to 0 to disable.</summary>
        public int ImapsPort { get; set; } = 993;

        /// <summary>TCP port for implicit-TLS SMTP (SMTPS). Defaults to 465. Set to 0 to disable.</summary>
        public int SmtpsPort { get; set; } = 465;

        /// <summary>Path where the auto-generated CA certificate (.cer) is exported for client trust.</summary>
        public string CaCertExportPath { get; set; } = "/app/certs/mailarchiver-ca.cer";

        /// <summary>Path where the auto-generated server PFX is exported so the SMTP service can share it.</summary>
        public string ServerCertExportPath { get; set; } = "/app/certs/mailarchiver-server.pfx";
    }
}
