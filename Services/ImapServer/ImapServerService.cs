using MailArchiver.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MailArchiver.Services.ImapServer
{
    /// <summary>
    /// A BackgroundService that listens for IMAP client connections and spawns
    /// an ImapSession for each one. Enabled via appsettings.json "ImapServer:Enabled".
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

            var startTlsEnabled = _options.EnableStartTls;
            X509Certificate2? tlsCertificate = null;

            if (startTlsEnabled)
            {
                if (!string.IsNullOrWhiteSpace(_options.TlsCertificatePath))
                {
                    try
                    {
                        var certPath = _options.TlsCertificatePath;
                        if (!File.Exists(certPath))
                        {
                            _logger.LogWarning("ImapServer TLS certificate file was not found at '{CertPath}'. Falling back to self-signed certificate.", certPath);
                            tlsCertificate = GenerateSelfSignedCertificate();
                        }
                        else
                        {
                            tlsCertificate = new X509Certificate2(
                                certPath,
                                _options.TlsCertificatePassword,
                                X509KeyStorageFlags.EphemeralKeySet);
                            _logger.LogInformation("Loaded IMAP TLS certificate from {CertPath}", certPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load ImapServer TLS certificate. Falling back to self-signed certificate.");
                        tlsCertificate = GenerateSelfSignedCertificate();
                    }
                }
                else
                {
                    // No certificate path provided — auto-generate a self-signed certificate
                    tlsCertificate = GenerateSelfSignedCertificate();
                    _logger.LogInformation("Generated self-signed certificate for IMAP STARTTLS");
                }
            }

            if (!IPAddress.TryParse(_options.Host, out var bindAddress))
            {
                _logger.LogError("Invalid ImapServer:Host value '{Host}'. Using 0.0.0.0.", _options.Host);
                bindAddress = IPAddress.Any;
            }

            var listener = new TcpListener(bindAddress, _options.Port);

            try
            {
                listener.Start();
                _logger.LogInformation(
                    "Built-in IMAP server started on {Host}:{Port}. STARTTLS: {StartTlsEnabled}, Require STARTTLS before auth: {RequireStartTls}",
                    _options.Host,
                    _options.Port,
                    startTlsEnabled,
                    startTlsEnabled && _options.RequireStartTls);

                while (!stoppingToken.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "IMAP server: error accepting connection");
                        continue;
                    }

                    // Fire-and-forget: handle each client in its own task
                    _ = Task.Run(async () =>
                    {
                        var session = new ImapSession(
                            client,
                            _scopeFactory,
                            _logger,
                            startTlsEnabled,
                            tlsCertificate,
                            _options.RequireStartTls);
                        try
                        {
                            await session.HandleAsync(stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Unhandled error in IMAP session");
                        }
                    }, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "IMAP server fatal error on {Host}:{Port}", _options.Host, _options.Port);
            }
            finally
            {
                listener.Stop();
                tlsCertificate?.Dispose();
                _logger.LogInformation("Built-in IMAP server stopped");
            }
        }

        private static X509Certificate2 GenerateSelfSignedCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=MailArchiver IMAP Server",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

            var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
        }
    }
}
