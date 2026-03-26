using MailArchiver.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MailArchiver.Services.ImapServer
{
    /// <summary>
    /// A fake SMTP server that accepts connections and pretends to send emails
    /// but silently discards everything. This keeps Apple Mail happy without
    /// needing real SMTP credentials for read-only archive accounts.
    /// </summary>
    public class FakeSmtpService : BackgroundService
    {
        private readonly ImapServerOptions _options;
        private readonly ILogger<FakeSmtpService> _logger;

        public FakeSmtpService(
            IOptions<ImapServerOptions> options,
            ILogger<FakeSmtpService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled || !_options.SmtpEnabled)
            {
                _logger.LogInformation("Built-in fake SMTP server is disabled.");
                return;
            }

            var listener = new TcpListener(IPAddress.Parse(_options.Host), _options.SmtpPort);
            listener.Start();
            _logger.LogInformation("Fake SMTP server started on {Host}:{Port} — accepts all mail, sends nothing.",
                _options.Host, _options.SmtpPort);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleClientAsync(client, stoppingToken);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _logger.LogDebug("Fake SMTP connection from {Remote}", remoteEp);

            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                    // SMTP greeting
                    await writer.WriteLineAsync("220 mailarchiver ESMTP Fake SMTP Ready");

                    bool inData = false;

                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line == null) break;

                        _logger.LogDebug("SMTP << {Line}", line);

                        if (inData)
                        {
                            // In DATA mode, wait for the lone "." to end the message
                            if (line == ".")
                            {
                                inData = false;
                                await writer.WriteLineAsync("250 2.0.0 Ok: message discarded (archive is read-only)");
                            }
                            // Otherwise just consume the line silently
                            continue;
                        }

                        var upper = line.Trim().ToUpperInvariant();

                        if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                        {
                            await writer.WriteLineAsync("250-mailarchiver Hello");
                            await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                            await writer.WriteLineAsync("250-SIZE 52428800");
                            await writer.WriteLineAsync("250 OK");
                        }
                        else if (upper.StartsWith("AUTH"))
                        {
                            // Accept any authentication attempt
                            if (upper.Contains("PLAIN"))
                            {
                                // Could be inline or next line
                                var parts = line.Trim().Split(' ');
                                if (parts.Length >= 3)
                                {
                                    // Inline auth: AUTH PLAIN <base64>
                                    await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                                }
                                else
                                {
                                    // Need continuation
                                    await writer.WriteLineAsync("334 ");
                                    await reader.ReadLineAsync(ct); // consume the base64
                                    await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                                }
                            }
                            else if (upper.Contains("LOGIN"))
                            {
                                await writer.WriteLineAsync("334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Username:")));
                                await reader.ReadLineAsync(ct); // consume username
                                await writer.WriteLineAsync("334 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Password:")));
                                await reader.ReadLineAsync(ct); // consume password
                                await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                            }
                            else
                            {
                                await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                            }
                        }
                        else if (upper.StartsWith("MAIL FROM"))
                        {
                            await writer.WriteLineAsync("250 2.1.0 Ok");
                        }
                        else if (upper.StartsWith("RCPT TO"))
                        {
                            await writer.WriteLineAsync("250 2.1.5 Ok");
                        }
                        else if (upper.StartsWith("DATA"))
                        {
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            inData = true;
                        }
                        else if (upper.StartsWith("QUIT"))
                        {
                            await writer.WriteLineAsync("221 2.0.0 Bye");
                            break;
                        }
                        else if (upper.StartsWith("NOOP"))
                        {
                            await writer.WriteLineAsync("250 2.0.0 Ok");
                        }
                        else if (upper.StartsWith("RSET"))
                        {
                            await writer.WriteLineAsync("250 2.0.0 Ok");
                        }
                        else if (upper.StartsWith("STARTTLS"))
                        {
                            // We don't support TLS on the fake SMTP, just reject it
                            await writer.WriteLineAsync("454 4.7.0 TLS not available");
                        }
                        else
                        {
                            await writer.WriteLineAsync("250 2.0.0 Ok");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Fake SMTP session error from {Remote}: {Error}", remoteEp, ex.Message);
            }
        }
    }
}
