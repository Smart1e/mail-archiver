using MailArchiver.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MailArchiver.Services.ImapServer
{
    /// <summary>
    /// A BackgroundService that listens for IMAP client connections and spawns
    /// an ImapSession for each one. Enabled via appsettings.json "ImapServer:Enabled".
    ///
    /// Supports:
    ///   - Plain-text IMAP on Port (default 143) with optional STARTTLS
    ///   - Implicit-TLS IMAPS on ImapsPort (default 993) for Apple Mail / modern clients
    ///
    /// A self-signed CA certificate is auto-generated and used to sign the server
    /// certificate. The CA cert is exported to CaCertExportPath so it can be embedded
    /// in .mobileconfig profiles for automatic trust on Apple devices.
    /// </summary>
    public class ImapServerService : BackgroundService
    {
        private readonly ImapServerOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ImapServerService> _logger;

        public ImapServerService(
            IOptions<ImapServerOptions> options,
            IServiceScopeFactory scopeFactory,
            ILogger<ImapServerService> logger)
        {
            _options = options.Value;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Built-in IMAP server is disabled. Set ImapServer:Enabled=true in appsettings.json to enable.");
                return;
            }

            if (!IPAddress.TryParse(_options.Host, out var bindAddress))
            {
                _logger.LogError("Invalid ImapServer:Host value '{Host}'. Using 0.0.0.0.", _options.Host);
                bindAddress = IPAddress.Any;
            }

            // ── Certificate setup ──────────────────────────────────────────
            X509Certificate2? tlsCertificate = null;

            if (!string.IsNullOrWhiteSpace(_options.TlsCertificatePath) && File.Exists(_options.TlsCertificatePath))
            {
                try
                {
                    tlsCertificate = new X509Certificate2(
                        _options.TlsCertificatePath,
                        _options.TlsCertificatePassword,
                        X509KeyStorageFlags.EphemeralKeySet);
                    _logger.LogInformation("Loaded TLS certificate from {CertPath}", _options.TlsCertificatePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load TLS certificate from {CertPath}. Will auto-generate.", _options.TlsCertificatePath);
                }
            }

            if (tlsCertificate == null)
            {
                // Try to load previously generated certs (persistent across restarts)
                var serverPfxPath = _options.ServerCertExportPath;
                var caCerPath = _options.CaCertExportPath;

                if (File.Exists(serverPfxPath) && File.Exists(caCerPath))
                {
                    try
                    {
                        tlsCertificate = new X509Certificate2(
                            serverPfxPath,
                            (string?)null,
                            X509KeyStorageFlags.EphemeralKeySet);
                        _logger.LogInformation("Loaded existing server certificate from {Path} (persistent)", serverPfxPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load existing server cert — will regenerate");
                        tlsCertificate = null;
                    }
                }

                if (tlsCertificate == null)
                {
                    var (caCert, serverCert) = GenerateCaAndServerCertificate();
                    tlsCertificate = serverCert;

                    // Export the CA certificate so it can be embedded in .mobileconfig profiles
                    ExportCaCertificate(caCert);
                    // Export the server PFX so the fake SMTP service can share it
                    ExportServerCertificate(serverCert);
                    caCert.Dispose();
                    _logger.LogInformation("Generated and saved new CA + server certificates (will persist across restarts)");
                }
            }

            // ── Start listeners ────────────────────────────────────────────
            var tasks = new List<Task>();

            // Plain-text IMAP (port 143)
            if (_options.Port > 0)
            {
                tasks.Add(RunPlainTextListener(bindAddress, _options.Port, tlsCertificate, stoppingToken));
            }

            // Implicit-TLS IMAPS (port 993)
            if (_options.ImapsPort > 0)
            {
                tasks.Add(RunImplicitTlsListener(bindAddress, _options.ImapsPort, tlsCertificate, stoppingToken));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                tlsCertificate.Dispose();
                _logger.LogInformation("Built-in IMAP server stopped");
            }
        }

        // ── Plain-text IMAP listener (with optional STARTTLS) ──────────────

        private async Task RunPlainTextListener(IPAddress bindAddress, int port,
            X509Certificate2 tlsCertificate, CancellationToken ct)
        {
            var listener = new TcpListener(bindAddress, port);
            try
            {
                listener.Start();
                _logger.LogInformation("IMAP plain-text listener started on {Host}:{Port}", _options.Host, port);

                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogWarning(ex, "IMAP accept error on port {Port}", port); continue; }

                    _ = Task.Run(async () =>
                    {
                        var session = new ImapSession(client, _scopeFactory, _logger,
                            _options.EnableStartTls, tlsCertificate, _options.RequireStartTls);
                        try { await session.HandleAsync(ct); }
                        catch (Exception ex) { _logger.LogWarning(ex, "IMAP session error"); }
                    }, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "IMAP plain-text listener fatal error on port {Port}", port);
            }
            finally { listener.Stop(); }
        }

        // ── Implicit-TLS IMAPS listener ────────────────────────────────────

        private async Task RunImplicitTlsListener(IPAddress bindAddress, int port,
            X509Certificate2 tlsCertificate, CancellationToken ct)
        {
            var listener = new TcpListener(bindAddress, port);
            try
            {
                listener.Start();
                _logger.LogInformation("IMAPS implicit-TLS listener started on {Host}:{Port}", _options.Host, port);

                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogWarning(ex, "IMAPS accept error on port {Port}", port); continue; }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var sslStream = new SslStream(client.GetStream(), false);
                            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificate = tlsCertificate,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                ClientCertificateRequired = false,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                            }, ct);

                            // Create session that's already TLS — no STARTTLS needed
                            var session = new ImapSession(client, _scopeFactory, _logger,
                                startTlsEnabled: false, tlsCertificate: null, requireStartTls: false,
                                preAuthenticatedStream: sslStream);
                            await session.HandleAsync(ct);
                        }
                        catch (AuthenticationException ex)
                        {
                            _logger.LogDebug("IMAPS TLS handshake failed: {Error}", ex.Message);
                            client.Close();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "IMAPS session error");
                            client.Close();
                        }
                    }, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "IMAPS listener fatal error on port {Port}", port);
            }
            finally { listener.Stop(); }
        }

        // ── Certificate generation ─────────────────────────────────────────

        /// <summary>
        /// Generates a self-signed CA certificate and a server certificate signed by that CA.
        /// The CA cert can be trusted by clients; the server cert is used for TLS.
        /// </summary>
        private (X509Certificate2 CaCert, X509Certificate2 ServerCert) GenerateCaAndServerCertificate()
        {
            // ── 1. Create the CA ──
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest(
                "CN=MailArchiver CA, O=MailArchiver",
                caKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            caRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

            var caCert = caRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10));

            // ── 2. Create the server cert signed by the CA ──
            using var serverKey = RSA.Create(2048);
            var serverRequest = new CertificateRequest(
                "CN=MailArchiver Server, O=MailArchiver",
                serverKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            serverRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

            serverRequest.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // TLS Server

            // Subject Alternative Names — include common IPs and localhost
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddIpAddress(IPAddress.Parse("172.16.10.106"));
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName("mailarchiver");
            sanBuilder.AddDnsName("mailarchiver.local");
            serverRequest.CertificateExtensions.Add(sanBuilder.Build());

            // Sign with CA
            var serialNumber = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(serialNumber);

            var serverCertPub = serverRequest.Create(
                caCert,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(10),
                serialNumber);

            // Combine public cert with private key
            var serverCertWithKey = serverCertPub.CopyWithPrivateKey(serverKey);
            var exportable = new X509Certificate2(
                serverCertWithKey.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.EphemeralKeySet);

            _logger.LogInformation("Generated CA + server certificate for TLS (valid 10 years)");

            return (new X509Certificate2(caCert.Export(X509ContentType.Cert)), exportable);
        }

        /// <summary>
        /// Exports the CA certificate to a .cer file so it can be distributed to clients.
        /// </summary>
        private void ExportCaCertificate(X509Certificate2 caCert)
        {
            try
            {
                var exportPath = _options.CaCertExportPath;
                var dir = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var certBytes = caCert.Export(X509ContentType.Cert);
                File.WriteAllBytes(exportPath, certBytes);
                _logger.LogInformation("CA certificate exported to {Path} — embed this in .mobileconfig profiles for client trust", exportPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to export CA certificate to {Path}", _options.CaCertExportPath);
            }
        }

        /// <summary>
        /// Exports the server certificate as PFX so the FakeSmtpService can load the same cert.
        /// </summary>
        private void ExportServerCertificate(X509Certificate2 serverCert)
        {
            try
            {
                var exportPath = _options.ServerCertExportPath;
                var dir = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var pfxBytes = serverCert.Export(X509ContentType.Pfx);
                File.WriteAllBytes(exportPath, pfxBytes);
                _logger.LogInformation("Server certificate exported to {Path} for SMTP service", exportPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to export server certificate to {Path}", _options.ServerCertExportPath);
            }
        }
    }
}
