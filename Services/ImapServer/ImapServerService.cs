using MailArchiver.Models;
using Microsoft.Extensions.Options;
using System.Formats.Asn1;
using System.Net;
using System.Net.NetworkInformation;
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
                var desiredSans = ResolveSubjectAlternativeNames();

                // Try to load previously generated certs (persistent across restarts),
                // but regenerate if the SAN set no longer matches what the config asks for.
                var serverPfxPath = _options.ServerCertExportPath;
                var caCerPath = _options.CaCertExportPath;

                if (File.Exists(serverPfxPath) && File.Exists(caCerPath))
                {
                    try
                    {
                        var existing = new X509Certificate2(
                            serverPfxPath,
                            (string?)null,
                            X509KeyStorageFlags.EphemeralKeySet);

                        // Regenerate if SANs drifted, OR the leaf is within 30 days of
                        // expiry (server leaf is capped at 395 days to satisfy Apple's
                        // 398-day maximum — so we have to roll it over annually).
                        var expiresSoon = existing.NotAfter - DateTime.UtcNow < TimeSpan.FromDays(30);
                        if (!CertificateCoversSans(existing, desiredSans))
                        {
                            existing.Dispose();
                            _logger.LogInformation("Existing server cert SAN set does not cover current config — will regenerate");
                        }
                        else if (expiresSoon)
                        {
                            existing.Dispose();
                            _logger.LogInformation("Existing server cert expires {Expiry:u} (<30 days) — will regenerate", existing.NotAfter);
                        }
                        else
                        {
                            tlsCertificate = existing;
                            _logger.LogInformation("Loaded existing server certificate from {Path} (SAN set OK, expires {Expiry:u})", serverPfxPath, existing.NotAfter);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load existing server cert — will regenerate");
                        tlsCertificate = null;
                    }
                }

                if (tlsCertificate == null)
                {
                    var (caCert, serverCert) = GenerateCaAndServerCertificate(desiredSans);
                    tlsCertificate = serverCert;

                    // Export the CA certificate so it can be embedded in .mobileconfig profiles
                    ExportCaCertificate(caCert);
                    // Export the server PFX so the fake SMTP service can share it
                    ExportServerCertificate(serverCert);
                    caCert.Dispose();
                    _logger.LogInformation("Generated and saved new CA + server certificates with SANs: {Sans}", string.Join(", ", desiredSans));
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
        /// Collects SAN entries for the auto-generated server cert. Always includes loopback and
        /// localhost, plus every non-loopback IPv4 address on local interfaces (so LAN clients work).
        /// Adds TAILSCALE_IP if set, then any extra entries from ImapServer:SubjectAlternativeNames.
        /// Entries that parse as IP addresses are added as IP SANs; everything else becomes a DNS SAN.
        /// </summary>
        private List<string> ResolveSubjectAlternativeNames()
        {
            var sans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "localhost",
                "127.0.0.1",
                "::1",
                "mailarchiver",
                "mailarchiver.local",
            };

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (IPAddress.IsLoopback(ip.Address)) continue;
                    if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    sans.Add(ip.Address.ToString());
                }
            }

            var tailscaleIp = Environment.GetEnvironmentVariable("TAILSCALE_IP");
            if (!string.IsNullOrWhiteSpace(tailscaleIp))
                sans.Add(tailscaleIp.Trim());

            foreach (var extra in _options.SubjectAlternativeNames ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(extra))
                    sans.Add(extra.Trim());
            }

            return sans.ToList();
        }

        /// <summary>
        /// Returns true if the certificate's SAN extension covers every entry in the desired set.
        /// Used to decide whether to regenerate when the configured SAN list changes.
        /// </summary>
        private static bool CertificateCoversSans(X509Certificate2 cert, IEnumerable<string> desiredSans)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in cert.Extensions)
            {
                if (ext.Oid?.Value != "2.5.29.17") continue; // subjectAltName
                try
                {
                    var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
                    var seq = reader.ReadSequence();
                    while (seq.HasData)
                    {
                        var tag = seq.PeekTag();
                        if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
                        {
                            // dNSName — IA5String
                            found.Add(seq.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                        }
                        else if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 7)
                        {
                            // iPAddress — OCTET STRING (4 bytes IPv4 or 16 bytes IPv6)
                            var bytes = seq.ReadOctetString(tag);
                            try { found.Add(new IPAddress(bytes).ToString()); } catch { }
                        }
                        else
                        {
                            seq.ReadEncodedValue();
                        }
                    }
                }
                catch
                {
                    return false;
                }
            }

            foreach (var want in desiredSans)
            {
                if (!found.Contains(want)) return false;
            }
            return true;
        }

        /// <summary>
        /// Generates a self-signed CA certificate and a server certificate signed by that CA.
        /// The CA cert can be trusted by clients; the server cert is used for TLS.
        /// </summary>
        private (X509Certificate2 CaCert, X509Certificate2 ServerCert) GenerateCaAndServerCertificate(IEnumerable<string> sanEntries)
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

            // Subject Alternative Names — resolved dynamically from local interfaces + config
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var entry in sanEntries)
            {
                if (IPAddress.TryParse(entry, out var ip))
                    sanBuilder.AddIpAddress(ip);
                else
                    sanBuilder.AddDnsName(entry);
            }
            serverRequest.CertificateExtensions.Add(sanBuilder.Build());

            // Sign with CA
            var serialNumber = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(serialNumber);

            // Apple enforces a 398-day maximum validity on TLS server leaf certs
            // (macOS 10.15+, iOS 13+) even when the chain terminates at a user-installed
            // self-signed root. A longer-lived cert passes openssl's handshake but is
            // silently rejected by the system trust evaluator, which makes profile
            // installs and Apple Mail account verification fail with "timed out".
            // Keep CA at 10 years (roots are exempt) and cap the leaf at 395 days.
            var serverCertPub = serverRequest.Create(
                caCert,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(395),
                serialNumber);

            // Combine public cert with private key
            var serverCertWithKey = serverCertPub.CopyWithPrivateKey(serverKey);
            var exportable = new X509Certificate2(
                serverCertWithKey.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.EphemeralKeySet);

            _logger.LogInformation("Generated CA (10-year) and server leaf cert (395-day) for TLS");

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
