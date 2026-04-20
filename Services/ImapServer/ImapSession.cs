using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MailArchiver.Services.ImapServer
{
    /// <summary>
    /// Handles a single IMAP client connection, implementing a read-only subset of RFC 3501
    /// sufficient for mail clients (Thunderbird, Apple Mail, Outlook) to browse the archive.
    /// </summary>
    public class ImapSession
    {
        private enum SessionState { NotAuthenticated, Authenticated, Selected, Logout }
        private enum CommandResult { Continue, UpgradeToTlsRequested }

        private readonly TcpClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;
        private readonly bool _startTlsEnabled;
        private readonly X509Certificate2? _tlsCertificate;
        private readonly bool _requireStartTls;
        private readonly Stream? _preAuthenticatedStream;

        private SessionState _state = SessionState.NotAuthenticated;
        private bool _isTls;
        private MailAccount? _account;
        private string? _selectedFolder;
        private List<ArchivedEmail> _folderMessages = new();

        public ImapSession(
            TcpClient client,
            IServiceScopeFactory scopeFactory,
            ILogger logger,
            bool startTlsEnabled,
            X509Certificate2? tlsCertificate,
            bool requireStartTls,
            Stream? preAuthenticatedStream = null)
        {
            _client = client;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _startTlsEnabled = startTlsEnabled;
            _tlsCertificate = tlsCertificate;
            _requireStartTls = requireStartTls;
            _preAuthenticatedStream = preAuthenticatedStream;
        }

        public async Task HandleAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

            Stream stream = _preAuthenticatedStream ?? (Stream)_client.GetStream();
            _isTls = _preAuthenticatedStream != null; // Already TLS if we got a pre-authenticated stream
            var (reader, writer) = CreateTextAdapters(stream);

            var remote = _client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _logger.LogInformation("IMAP client connected from {Remote} (TLS: {IsTls})", remote, _isTls);

            try
            {
                await writer.WriteLineAsync($"* OK [CAPABILITY {BuildCapability()}] MailArchiver IMAP4rev1 Server Ready");
                await writer.FlushAsync(ct);

                while (!ct.IsCancellationRequested && _state != SessionState.Logout)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break; // client disconnected

                    _logger.LogInformation("IMAP << {Line}", line);

                    var result = await ProcessCommandAsync(line, reader, writer, db, ct);
                    await writer.FlushAsync(ct);

                    if (result == CommandResult.UpgradeToTlsRequested)
                    {
                        if (_tlsCertificate == null)
                        {
                            _logger.LogWarning("STARTTLS was requested but no TLS certificate is available.");
                            break;
                        }

                        var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);

                        try
                        {
                            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificate = _tlsCertificate,
                                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                ClientCertificateRequired = false,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                            }, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to negotiate STARTTLS for client {Remote}", remote);
                            break;
                        }

                        // Reinitialize text adapters after the protocol switch.
                        reader.Dispose();
                        writer.Dispose();
                        stream = sslStream;
                        (reader, writer) = CreateTextAdapters(stream);

                        _isTls = true;
                        _state = SessionState.NotAuthenticated;
                        _account = null;
                        _selectedFolder = null;
                        _folderMessages.Clear();

                        _logger.LogInformation("STARTTLS negotiated successfully for client {Remote}", remote);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // client disconnected mid-stream
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IMAP session error from {Remote}", remote);
            }
            finally
            {
                reader.Dispose();
                writer.Dispose();
                stream.Dispose();
                _client.Close();
                _logger.LogInformation("IMAP client disconnected from {Remote}", remote);
            }
        }

        private static (StreamReader Reader, StreamWriter Writer) CreateTextAdapters(Stream stream)
        {
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = false
            };

            return (reader, writer);
        }

        private string BuildCapability()
        {
            // These extensions make Apple Mail happy:
            //   ID, NAMESPACE — expected during initial handshake, Apple Mail logs errors without them.
            //   IDLE          — enables push-style updates instead of polling every few seconds.
            //   SPECIAL-USE   — lets Apple Mail map Sent/Trash/Archive folders to the right icons/behaviors.
            //   UIDPLUS       — advertised so clients know UID commands behave sensibly (we're UID-stable).
            //   LITERAL+      — non-synchronizing literals, Apple Mail uses these heavily.
            var capabilities = new List<string>
            {
                "IMAP4rev1",
                "LITERAL+",
                "ID",
                "NAMESPACE",
                "IDLE",
                "SPECIAL-USE",
                "UIDPLUS",
            };

            if (_startTlsEnabled && !_isTls)
            {
                capabilities.Add("STARTTLS");
            }

            if (_requireStartTls && !_isTls)
            {
                capabilities.Add("LOGINDISABLED");
            }
            else
            {
                capabilities.Add("AUTH=PLAIN");
                capabilities.Add("AUTH=LOGIN");
            }

            return string.Join(" ", capabilities);
        }

        // -----------------------------------------------------------------------
        // Command dispatch
        // -----------------------------------------------------------------------

        private async Task<CommandResult> ProcessCommandAsync(string line, StreamReader reader, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            // Parse: TAG SP COMMAND [SP ARGS]
            var parts = line.Split(' ', 3);
            if (parts.Length < 2)
            {
                await writer.WriteLineAsync("* BAD Invalid command");
                return CommandResult.Continue;
            }

            var tag = parts[0];
            var command = parts[1].ToUpperInvariant();
            var args = parts.Length > 2 ? parts[2] : "";

            _logger.LogDebug("IMAP tag={Tag} cmd={Command}", tag, command);

            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync($"* CAPABILITY {BuildCapability()}");
                    await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                    break;

                case "STARTTLS":
                    if (!_startTlsEnabled || _tlsCertificate == null)
                    {
                        await writer.WriteLineAsync($"{tag} NO STARTTLS not available");
                        break;
                    }

                    if (_isTls)
                    {
                        await writer.WriteLineAsync($"{tag} BAD Already using TLS");
                        break;
                    }

                    if (_state != SessionState.NotAuthenticated)
                    {
                        await writer.WriteLineAsync($"{tag} BAD STARTTLS only valid in not-authenticated state");
                        break;
                    }

                    await writer.WriteLineAsync($"{tag} OK Begin TLS negotiation now");
                    return CommandResult.UpgradeToTlsRequested;

                case "NOOP":
                    await writer.WriteLineAsync($"{tag} OK NOOP completed");
                    break;

                case "ID":
                    // Apple Mail sends ID right after greeting and expects an ID response.
                    // We identify as MailArchiver but don't echo the client-supplied values.
                    await writer.WriteLineAsync($"* ID (\"name\" \"MailArchiver\" \"version\" \"{GetAssemblyVersion()}\")");
                    await writer.WriteLineAsync($"{tag} OK ID completed");
                    break;

                case "NAMESPACE":
                    // Single personal namespace, "/" as hierarchy delimiter. No shared or other-users.
                    await writer.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL");
                    await writer.WriteLineAsync($"{tag} OK NAMESPACE completed");
                    break;

                case "IDLE":
                    await HandleIdleAsync(tag, reader, writer, ct);
                    break;

                case "ENABLE":
                    // We don't actually honor any extensions, but acknowledging keeps Apple Mail happy.
                    await writer.WriteLineAsync($"{tag} OK ENABLE completed");
                    break;

                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE MailArchiver IMAP4rev1 Server logging out");
                    await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                    _state = SessionState.Logout;
                    break;

                case "LOGIN":
                    if (_requireStartTls && !_isTls)
                    {
                        await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] STARTTLS is required before authentication");
                        break;
                    }
                    await HandleLoginAsync(tag, args, writer, db, ct);
                    break;

                case "AUTHENTICATE":
                    if (_requireStartTls && !_isTls)
                    {
                        await writer.WriteLineAsync($"{tag} NO [PRIVACYREQUIRED] STARTTLS is required before authentication");
                        break;
                    }
                    await HandleAuthenticateAsync(tag, args, writer, reader, db, ct);
                    break;

                case "LIST":
                    await HandleListAsync(tag, args, writer, db, ct);
                    break;

                case "LSUB":
                    await HandleListAsync(tag, args, writer, db, ct); // same as LIST for read-only server
                    break;

                case "STATUS":
                    await HandleStatusAsync(tag, args, writer, db, ct);
                    break;

                case "SELECT":
                case "EXAMINE":
                    await HandleSelectAsync(tag, args, writer, db, ct);
                    break;

                case "CLOSE":
                    if (_state == SessionState.Selected)
                    {
                        _state = SessionState.Authenticated;
                        _selectedFolder = null;
                        _folderMessages.Clear();
                    }
                    await writer.WriteLineAsync($"{tag} OK CLOSE completed");
                    break;

                case "EXPUNGE":
                    // Read-only: acknowledge without doing anything
                    await writer.WriteLineAsync($"{tag} OK EXPUNGE completed");
                    break;

                case "FETCH":
                    await HandleFetchAsync(tag, args, isUid: false, writer, db, ct);
                    break;

                case "UID":
                    await HandleUidCommandAsync(tag, args, writer, db, ct);
                    break;

                case "SEARCH":
                    await HandleSearchAsync(tag, args, isUid: false, writer, db, ct);
                    break;

                case "STORE":
                    // Read-only mailbox: acknowledge silently. Returning NO caused Apple Mail to
                    // treat every sync as a failure and repeatedly re-download all messages.
                    // The client's local flag state wins; we never persist changes.
                    _logger.LogDebug("STORE silent-OK folder={Folder} args={Args}", _selectedFolder ?? "<none>", args);
                    await writer.WriteLineAsync($"{tag} OK STORE completed");
                    break;

                case "COPY":
                    await writer.WriteLineAsync($"{tag} NO [CANNOT] Read-only archive cannot accept copies");
                    break;

                case "APPEND":
                    await writer.WriteLineAsync($"{tag} NO [CANNOT] Read-only archive cannot accept appended messages");
                    break;

                default:
                    await writer.WriteLineAsync($"{tag} BAD Unknown command {command}");
                    break;
            }

            return CommandResult.Continue;
        }

        // -----------------------------------------------------------------------
        // LOGIN
        // -----------------------------------------------------------------------

        private async Task HandleLoginAsync(string tag, string args, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            // Args: "username" "password"  or username password (possibly quoted)
            var (user, pass) = ParseTwoArgs(args);

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                await writer.WriteLineAsync($"{tag} BAD LOGIN missing credentials");
                return;
            }

            // Support "archive-user@domain" login format — strip the "archive-" prefix
            // so Apple Mail can use a unique username that doesn't clash with existing accounts.
            var lookupUser = user;
            if (lookupUser.StartsWith("archive-", StringComparison.OrdinalIgnoreCase))
                lookupUser = lookupUser.Substring(8);

            // Match against ArchiveImapUsername (if set) OR EmailAddress. Clients can now
            // expose the archive under a different address than the real sync account.
            var account = await db.MailAccounts
                .FirstOrDefaultAsync(a =>
                    (a.ArchiveImapUsername == lookupUser || a.EmailAddress == lookupUser)
                    && a.ImapPassword != null && a.ImapPassword == pass, ct);

            if (account == null)
            {
                _logger.LogWarning("IMAP LOGIN failed for user {User} (lookup: {LookupUser})", user, lookupUser);
                await writer.WriteLineAsync($"{tag} NO LOGIN failed");
                return;
            }

            _account = account;
            _state = SessionState.Authenticated;
            _logger.LogInformation("IMAP LOGIN succeeded for {User} → {LookupUser} (account: {AccountName})", user, lookupUser, account.Name);
            await writer.WriteLineAsync($"{tag} OK LOGIN completed");
        }

        // -----------------------------------------------------------------------
        // AUTHENTICATE (PLAIN and LOGIN mechanisms for Apple Mail / Outlook)
        // -----------------------------------------------------------------------

        private async Task HandleAuthenticateAsync(string tag, string args, StreamWriter writer,
            StreamReader reader, MailArchiverDbContext db, CancellationToken ct)
        {
            var mechanism = args.Trim().Split(' ')[0].ToUpperInvariant();
            // Inline initial response (e.g. AUTHENTICATE PLAIN <base64>)
            var inline = args.Trim().Contains(' ') ? args.Trim().Substring(args.Trim().IndexOf(' ') + 1).Trim() : null;

            if (mechanism != "PLAIN" && mechanism != "LOGIN")
            {
                await writer.WriteLineAsync($"{tag} NO Unsupported authentication mechanism");
                return;
            }

            string user, pass;

            if (mechanism == "PLAIN")
            {
                string b64;
                if (!string.IsNullOrEmpty(inline) && inline != "=")
                {
                    b64 = inline;
                }
                else
                {
                    // Send continuation and wait for base64 blob
                    await writer.WriteLineAsync("+ ");
                    await writer.FlushAsync(ct);
                    b64 = await reader.ReadLineAsync(ct) ?? "";
                }

                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    // Format: [authzid]\0authcid\0passwd
                    var parts = decoded.Split('\0');
                    user = parts.Length >= 3 ? parts[1] : (parts.Length == 2 ? parts[0] : "");
                    pass = parts.Length >= 3 ? parts[2] : (parts.Length == 2 ? parts[1] : "");
                }
                catch
                {
                    await writer.WriteLineAsync($"{tag} BAD Invalid base64 encoding");
                    return;
                }
            }
            else // LOGIN mechanism: server prompts for username then password
            {
                await writer.WriteLineAsync("+ " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Username:")));
                await writer.FlushAsync(ct);
                var userB64 = await reader.ReadLineAsync(ct) ?? "";

                await writer.WriteLineAsync("+ " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Password:")));
                await writer.FlushAsync(ct);
                var passB64 = await reader.ReadLineAsync(ct) ?? "";

                try
                {
                    user = Encoding.UTF8.GetString(Convert.FromBase64String(userB64));
                    pass = Encoding.UTF8.GetString(Convert.FromBase64String(passB64));
                }
                catch
                {
                    await writer.WriteLineAsync($"{tag} BAD Invalid base64 encoding");
                    return;
                }
            }

            // Support "archive-user@domain" login format — strip the "archive-" prefix
            var lookupUser = user;
            if (lookupUser.StartsWith("archive-", StringComparison.OrdinalIgnoreCase))
                lookupUser = lookupUser.Substring(8);

            // Match against ArchiveImapUsername (if set) OR EmailAddress.
            var account = await db.MailAccounts
                .FirstOrDefaultAsync(a =>
                    (a.ArchiveImapUsername == lookupUser || a.EmailAddress == lookupUser)
                    && a.ImapPassword != null && a.ImapPassword == pass, ct);

            if (account == null)
            {
                _logger.LogWarning("IMAP AUTHENTICATE failed for user {User} (lookup: {LookupUser})", user, lookupUser);
                await writer.WriteLineAsync($"{tag} NO AUTHENTICATE failed");
                return;
            }

            _account = account;
            _state = SessionState.Authenticated;
            _logger.LogInformation("IMAP AUTHENTICATE succeeded for {User} → {LookupUser} (account: {AccountName})", user, lookupUser, account.Name);
            await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed");
        }

        // -----------------------------------------------------------------------
        // LIST / LSUB
        // -----------------------------------------------------------------------

        private async Task HandleListAsync(string tag, string args, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            if (_state == SessionState.NotAuthenticated)
            {
                await writer.WriteLineAsync($"{tag} NO Not authenticated");
                return;
            }

            var folders = await db.ArchivedEmails
                .Where(e => e.MailAccountId == _account!.Id)
                .Select(e => e.FolderName)
                .Distinct()
                .OrderBy(f => f)
                .ToListAsync(ct);

            foreach (var folder in folders)
            {
                var encodedFolder = EncodeMailboxName(folder);
                var attrs = new List<string> { @"\HasNoChildren" };
                var specialUse = DetectSpecialUseFlag(folder);
                if (specialUse != null) attrs.Add(specialUse);
                await writer.WriteLineAsync($"* LIST ({string.Join(" ", attrs)}) \"/\" \"{encodedFolder}\"");
            }

            await writer.WriteLineAsync($"{tag} OK LIST completed");
        }

        // -----------------------------------------------------------------------
        // STATUS
        // -----------------------------------------------------------------------

        private async Task HandleStatusAsync(string tag, string args, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            if (_state == SessionState.NotAuthenticated)
            {
                await writer.WriteLineAsync($"{tag} NO Not authenticated");
                return;
            }

            var folder = ExtractMailboxName(args, out var remaining);
            var count = await db.ArchivedEmails
                .Where(e => e.MailAccountId == _account!.Id && e.FolderName == folder)
                .CountAsync(ct);

            var maxId = count > 0
                ? await db.ArchivedEmails
                    .Where(e => e.MailAccountId == _account!.Id && e.FolderName == folder)
                    .MaxAsync(e => e.Id, ct)
                : 0;

            // Use account ID as UIDVALIDITY — must match SELECT response
            var uidValidity = _account!.Id;
            await writer.WriteLineAsync($"* STATUS \"{EncodeMailboxName(folder)}\" (MESSAGES {count} RECENT 0 UIDNEXT {maxId + 1} UIDVALIDITY {uidValidity} UNSEEN 0)");
            await writer.WriteLineAsync($"{tag} OK STATUS completed");
        }

        // -----------------------------------------------------------------------
        // SELECT / EXAMINE
        // -----------------------------------------------------------------------

        private async Task HandleSelectAsync(string tag, string args, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            if (_state == SessionState.NotAuthenticated)
            {
                await writer.WriteLineAsync($"{tag} NO Not authenticated");
                return;
            }

            var folder = ExtractMailboxName(args, out _);

            // Load all messages for this folder, ordered by SentDate
            _folderMessages = await db.ArchivedEmails
                .Where(e => e.MailAccountId == _account!.Id && e.FolderName == folder)
                .Include(e => e.Attachments)
                .OrderBy(e => e.SentDate)
                .ThenBy(e => e.Id)
                .ToListAsync(ct);

            _selectedFolder = folder;
            _state = SessionState.Selected;

            var count = _folderMessages.Count;
            var uidNext = count > 0 ? _folderMessages.Max(e => e.Id) + 1 : 1;

            // Use account ID as UIDVALIDITY — stable across sessions, unique per account
            var uidValidity = _account!.Id;

            await writer.WriteLineAsync($"* {count} EXISTS");
            await writer.WriteLineAsync("* 0 RECENT");
            await writer.WriteLineAsync($"* OK [UIDVALIDITY {uidValidity}] UIDs valid");
            await writer.WriteLineAsync($"* OK [UIDNEXT {uidNext}] Predicted next UID");
            await writer.WriteLineAsync(@"* FLAGS (\Seen \Answered \Flagged \Deleted \Draft)");
            await writer.WriteLineAsync(@"* OK [PERMANENTFLAGS ()] Read-only mailbox");
            await writer.WriteLineAsync($"{tag} OK [READ-ONLY] SELECT completed");
        }

        // -----------------------------------------------------------------------
        // UID dispatcher
        // -----------------------------------------------------------------------

        private async Task HandleUidCommandAsync(string tag, string args, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            var parts = args.Split(' ', 2);
            var subCommand = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
            var subArgs = parts.Length > 1 ? parts[1] : "";

            switch (subCommand)
            {
                case "FETCH":
                    await HandleFetchAsync(tag, subArgs, isUid: true, writer, db, ct);
                    break;
                case "SEARCH":
                    await HandleSearchAsync(tag, subArgs, isUid: true, writer, db, ct);
                    break;
                case "STORE":
                    _logger.LogDebug("UID STORE silent-OK folder={Folder} args={Args}", _selectedFolder ?? "<none>", subArgs);
                    await writer.WriteLineAsync($"{tag} OK UID STORE completed");
                    break;
                case "COPY":
                    await writer.WriteLineAsync($"{tag} NO [CANNOT] Read-only archive cannot accept copies");
                    break;
                default:
                    await writer.WriteLineAsync($"{tag} BAD Unknown UID command {subCommand}");
                    break;
            }
        }

        // -----------------------------------------------------------------------
        // SEARCH
        // -----------------------------------------------------------------------

        private async Task HandleSearchAsync(string tag, string args, bool isUid,
            StreamWriter writer, MailArchiverDbContext db, CancellationToken ct)
        {
            if (_state != SessionState.Selected)
            {
                await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                return;
            }

            var matching = new List<int>();
            var upperArgs = args.ToUpperInvariant().Trim();

            // Strip leading CHARSET clause (e.g. `CHARSET UTF-8 ALL` → `ALL`). Apple Mail prefixes it.
            if (upperArgs.StartsWith("CHARSET "))
            {
                var afterCharset = upperArgs.IndexOf(' ', 8);
                if (afterCharset > 0)
                {
                    args = args.Substring(afterCharset + 1).Trim();
                    upperArgs = args.ToUpperInvariant().Trim();
                }
            }

            for (int i = 0; i < _folderMessages.Count; i++)
            {
                var email = _folderMessages[i];
                bool match;

                // Treat whole-mailbox predicates as "match all" — our archive has no per-message
                // flag state, so UNSEEN/SEEN/FLAGGED/etc. all reduce to "all messages". This is
                // what Apple Mail actually needs during sync; it just wants a complete UID list.
                if (upperArgs == "ALL" || upperArgs == ""
                    || upperArgs == "UNSEEN" || upperArgs == "SEEN"
                    || upperArgs == "FLAGGED" || upperArgs == "UNFLAGGED"
                    || upperArgs == "ANSWERED" || upperArgs == "UNANSWERED"
                    || upperArgs == "DRAFT" || upperArgs == "UNDRAFT"
                    || upperArgs == "DELETED" || upperArgs == "UNDELETED"
                    || upperArgs == "NEW" || upperArgs == "OLD" || upperArgs == "RECENT"
                    || upperArgs == "NOT DELETED")
                {
                    match = true;
                }
                else if (upperArgs.StartsWith("SINCE "))
                {
                    var dateStr = args.Substring(6).Trim().Trim('"');
                    match = DateTime.TryParse(dateStr, out var since) && email.SentDate >= since;
                }
                else if (upperArgs.StartsWith("BEFORE "))
                {
                    var dateStr = args.Substring(7).Trim().Trim('"');
                    match = DateTime.TryParse(dateStr, out var before) && email.SentDate < before;
                }
                else if (upperArgs.StartsWith("ON "))
                {
                    var dateStr = args.Substring(3).Trim().Trim('"');
                    match = DateTime.TryParse(dateStr, out var on) && email.SentDate.Date == on.Date;
                }
                else if (upperArgs.StartsWith("SUBJECT "))
                {
                    var term = args.Substring(8).Trim().Trim('"');
                    match = email.Subject?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;
                }
                else if (upperArgs.StartsWith("FROM "))
                {
                    var term = args.Substring(5).Trim().Trim('"');
                    match = email.From?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;
                }
                else if (upperArgs.StartsWith("TO "))
                {
                    var term = args.Substring(3).Trim().Trim('"');
                    match = email.To?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;
                }
                else if (upperArgs.StartsWith("CC "))
                {
                    var term = args.Substring(3).Trim().Trim('"');
                    match = email.Cc?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;
                }
                else if (upperArgs.StartsWith("BODY ") || upperArgs.StartsWith("TEXT "))
                {
                    var skip = upperArgs.StartsWith("BODY ") ? 5 : 5;
                    var term = args.Substring(skip).Trim().Trim('"');
                    match = (email.Body?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (email.HtmlBody?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                         || (email.Subject?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
                }
                else if (upperArgs.StartsWith("HEADER "))
                {
                    // HEADER <field> <value> — only Message-ID commonly queried by Apple Mail
                    var rest = args.Substring(7).Trim();
                    var fieldEnd = rest.IndexOf(' ');
                    if (fieldEnd > 0)
                    {
                        var field = rest.Substring(0, fieldEnd);
                        var term = rest.Substring(fieldEnd + 1).Trim().Trim('"');
                        if (string.Equals(field, "Message-ID", StringComparison.OrdinalIgnoreCase))
                            match = email.MessageId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false;
                        else
                            match = true; // unknown header — don't filter anything out
                    }
                    else
                    {
                        match = true;
                    }
                }
                else
                {
                    // Unknown criterion — be permissive so Apple Mail's sync doesn't stall.
                    match = true;
                }

                if (match)
                    matching.Add(isUid ? email.Id : (i + 1)); // UID vs MSN
            }

            await writer.WriteLineAsync($"* SEARCH {string.Join(" ", matching)}");
            await writer.WriteLineAsync($"{tag} OK SEARCH completed");
        }

        // -----------------------------------------------------------------------
        // FETCH
        // -----------------------------------------------------------------------

        private async Task HandleFetchAsync(string tag, string args, bool isUid,
            StreamWriter writer, MailArchiverDbContext db, CancellationToken ct)
        {
            if (_state != SessionState.Selected)
            {
                await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                return;
            }

            // Parse: set items   e.g.  1:* (FLAGS UID RFC822.SIZE)
            var spaceIdx = args.IndexOf(' ');
            if (spaceIdx < 0)
            {
                await writer.WriteLineAsync($"{tag} BAD FETCH missing arguments");
                return;
            }

            var setStr = args.Substring(0, spaceIdx);
            var itemsStr = args.Substring(spaceIdx + 1).Trim();

            _logger.LogDebug("FETCH request isUid={IsUid} set={Set} items={Items}", isUid, setStr, itemsStr);

            var indices = ExpandMessageSet(setStr, isUid);

            foreach (var idx in indices)
            {
                ArchivedEmail? email;
                int msn;

                if (isUid)
                {
                    email = _folderMessages.FirstOrDefault(e => e.Id == idx);
                    msn = email != null ? _folderMessages.IndexOf(email) + 1 : -1;
                }
                else
                {
                    if (idx < 1 || idx > _folderMessages.Count) continue;
                    msn = idx;
                    email = _folderMessages[idx - 1];
                }

                if (email == null) continue;

                await WriteFetchResponseAsync(msn, email, itemsStr, writer, ct);
            }

            // Persist any CachedRfc822Size values computed on first access so subsequent sessions
            // return the same stable value without recomputing.
            try
            {
                if (db.ChangeTracker.HasChanges())
                    await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist RFC822.SIZE cache updates; will retry on next FETCH");
            }

            await writer.WriteLineAsync($"{tag} OK FETCH completed");
        }

        private async Task WriteFetchResponseAsync(int msn, ArchivedEmail email, string itemsStr,
            StreamWriter writer, CancellationToken ct)
        {
            var items = ParseFetchItems(itemsStr);

            // Each part is either a plain string segment (no literal) OR a literal (item name + bytes).
            // We emit them in request order so clients that send multiple literals per FETCH
            // (Apple Mail pairs BODY.PEEK[HEADER.FIELDS ...] with BODY.PEEK[TEXT]) get every item back.
            var parts = new List<(string? plain, string? literalName, byte[]? literalBytes)>();
            int literalCount = 0;

            foreach (var item in items)
            {
                var upper = item.ToUpperInvariant();

                if (upper == "UID")
                    parts.Add(($"UID {email.Id}", null, null));
                else if (upper == "FLAGS")
                    parts.Add((@"FLAGS (\Seen)", null, null));
                else if (upper == "INTERNALDATE")
                    parts.Add(($"INTERNALDATE \"{FormatInternalDate(email.ReceivedDate)}\"", null, null));
                else if (upper == "RFC822.SIZE")
                    parts.Add(($"RFC822.SIZE {ImapMessageBuilder.GetMessageSize(email)}", null, null));
                else if (upper == "ENVELOPE")
                    parts.Add(($"ENVELOPE {ImapMessageBuilder.BuildEnvelope(email)}", null, null));
                else if (upper == "BODYSTRUCTURE" || upper == "BODY")
                    parts.Add(($"{upper} {ImapMessageBuilder.BuildBodyStructure(email)}", null, null));
                else if (upper == "RFC822" || upper == "BODY[]" || upper == "BODY.PEEK[]")
                {
                    var msg = ImapMessageBuilder.BuildMessage(email);
                    var bytes = ImapMessageBuilder.SerializeMessage(msg);
                    parts.Add((null, upper == "RFC822" ? "RFC822" : "BODY[]", bytes));
                    literalCount++;
                }
                else if (upper.StartsWith("BODY[HEADER") || upper.StartsWith("BODY.PEEK[HEADER"))
                {
                    var msg = ImapMessageBuilder.BuildMessage(email);
                    var openBracket = item.IndexOf('[');
                    var closeBracket = item.LastIndexOf(']');
                    var section = (openBracket >= 0 && closeBracket > openBracket)
                        ? item.Substring(openBracket + 1, closeBracket - openBracket - 1)
                        : "HEADER";
                    var sectionUpper = section.ToUpperInvariant();

                    using var ms = new MemoryStream();

                    if (sectionUpper.StartsWith("HEADER.FIELDS.NOT"))
                    {
                        // Apple Mail uses this during initial sync: "give me every header EXCEPT these"
                        var excluded = ParseHeaderFieldList(section);
                        foreach (var header in msg.Headers)
                        {
                            if (excluded.Any(f => string.Equals(f, header.Field, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            var line = $"{header.Field}: {header.Value}\r\n";
                            var bytes = Encoding.UTF8.GetBytes(line);
                            ms.Write(bytes, 0, bytes.Length);
                        }
                        ms.Write(Encoding.UTF8.GetBytes("\r\n"), 0, 2);
                    }
                    else if (sectionUpper.StartsWith("HEADER.FIELDS"))
                    {
                        // Most common in Apple Mail: "give me only these headers" for the message list.
                        // Without this path, Apple Mail falls through to BODY[] and fetches every message
                        // in full on first sync — slow, and it trips the re-download loop.
                        var wanted = ParseHeaderFieldList(section);
                        foreach (var field in wanted)
                        {
                            foreach (var header in msg.Headers)
                            {
                                if (!string.Equals(header.Field, field, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                var line = $"{header.Field}: {header.Value}\r\n";
                                var bytes = Encoding.UTF8.GetBytes(line);
                                ms.Write(bytes, 0, bytes.Length);
                            }
                        }
                        ms.Write(Encoding.UTF8.GetBytes("\r\n"), 0, 2);
                    }
                    else
                    {
                        // Plain BODY[HEADER] — full headers
                        msg.Headers.WriteTo(ImapMessageBuilder.CrlfFormat, ms);
                        ms.Write(Encoding.UTF8.GetBytes("\r\n"), 0, 2);
                    }

                    // Preserve the exact section string the client requested so the response matches
                    // (Apple Mail matches the item name in the reply against the one it sent).
                    parts.Add((null, $"BODY[{section}]", ms.ToArray()));
                    literalCount++;
                }
                else if (upper.StartsWith("BODY[TEXT") || upper.StartsWith("BODY.PEEK[TEXT"))
                {
                    var msg = ImapMessageBuilder.BuildMessage(email);
                    var fullBytes = ImapMessageBuilder.SerializeMessage(msg);
                    var fullStr = Encoding.UTF8.GetString(fullBytes);
                    // TEXT is everything after the first blank line (header/body separator)
                    var sepIdx = fullStr.IndexOf("\r\n\r\n");
                    var textBytes = sepIdx >= 0
                        ? Encoding.UTF8.GetBytes(fullStr.Substring(sepIdx + 4))
                        : Encoding.UTF8.GetBytes(fullStr);
                    parts.Add((null, "BODY[TEXT]", textBytes));
                    literalCount++;
                }
                else if (upper.StartsWith("BODY[") || upper.StartsWith("BODY.PEEK["))
                {
                    // Handle BODY[N], BODY[N.N], BODY[N.MIME], BODY.PEEK[N] etc.
                    // Extract the section spec between [ and ]
                    var openBracket = upper.IndexOf('[');
                    var closeBracket = upper.IndexOf(']');
                    if (openBracket >= 0 && closeBracket > openBracket)
                    {
                        var section = upper.Substring(openBracket + 1, closeBracket - openBracket - 1);
                        var msg = ImapMessageBuilder.BuildMessage(email);

                        byte[] partBytes;
                        if (string.IsNullOrEmpty(section))
                        {
                            partBytes = ImapMessageBuilder.SerializeMessage(msg);
                        }
                        else
                        {
                            // Try to resolve MIME part by section number (e.g. "1", "1.1", "2")
                            var partMime = ResolveMimePart(msg.Body, section);
                            if (partMime != null)
                            {
                                using var ms = new MemoryStream();
                                if (section.EndsWith(".MIME"))
                                {
                                    // BODY[N.MIME] — return only the MIME headers of the part
                                    partMime.Headers.WriteTo(ImapMessageBuilder.CrlfFormat, ms);
                                    ms.Write(Encoding.UTF8.GetBytes("\r\n"));
                                }
                                else if (partMime is MimeKit.MimePart mimePart && mimePart.Content != null)
                                {
                                    // BODY[N] for a leaf part — return only the encoded content,
                                    // NOT the MIME headers. Apple Mail expects raw content here.
                                    mimePart.Content.WriteTo(ms);
                                }
                                else if (partMime is MimeKit.Multipart)
                                {
                                    // BODY[N] for a multipart — write the full multipart body
                                    partMime.WriteTo(ImapMessageBuilder.CrlfFormat, ms);
                                }
                                else
                                {
                                    partMime.WriteTo(ImapMessageBuilder.CrlfFormat, ms);
                                }
                                partBytes = ms.ToArray();
                            }
                            else
                            {
                                partBytes = Array.Empty<byte>();
                            }
                        }
                        parts.Add((null, $"BODY[{item.Substring(item.IndexOf('[') + 1)}", partBytes));
                        literalCount++;
                    }
                }
            }

            // Drain any buffered writer text so it lands before our raw bytes.
            // StreamWriter has AutoFlush=false, so text from prior responses in the same batch
            // could otherwise overtake the binary that follows.
            await writer.FlushAsync(ct);
            var stream = writer.BaseStream;

            var prefix = $"* {msn} FETCH (";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(prefix), ct);

            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(" "), ct);
                var p = parts[i];
                if (p.plain != null)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(p.plain), ct);
                }
                else
                {
                    var head = Encoding.UTF8.GetBytes($"{p.literalName} {{{p.literalBytes!.Length}}}\r\n");
                    await stream.WriteAsync(head, ct);
                    await stream.WriteAsync(p.literalBytes, ct);
                }
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(")\r\n"), ct);
            await stream.FlushAsync(ct);

            _logger.LogDebug("FETCH reply msn={Msn} uid={Uid} parts={Parts} literals={Literals}",
                msn, email.Id, parts.Count, literalCount);
        }

        /// <summary>
        /// Formats a DateTime as an IMAP INTERNALDATE string in UTC. The DB returns DateTimeKind.Unspecified;
        /// treat that as UTC so the rendered date never drifts across midnight between sessions (Apple Mail
        /// treats a changing INTERNALDATE as changed metadata and may re-fetch).
        /// </summary>
        private static string FormatInternalDate(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return utc.ToString("dd-MMM-yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Expands an IMAP message set string (e.g. "1:*", "1,3,5:8") into a list of indices.
        /// When isUid=false, indices are 1-based MSNs capped to folder size.
        /// When isUid=true, indices are UID values (uncapped).
        /// </summary>
        private List<int> ExpandMessageSet(string set, bool isUid)
        {
            var result = new List<int>();
            int maxMsn = _folderMessages.Count;
            int maxUid = _folderMessages.Count > 0 ? _folderMessages.Max(e => e.Id) : 0;

            foreach (var range in set.Split(','))
            {
                var parts = range.Split(':');
                if (parts.Length == 1)
                {
                    if (int.TryParse(parts[0], out int val))
                        result.Add(val);
                }
                else
                {
                    var startStr = parts[0];
                    var endStr = parts[1];
                    int start = startStr == "*" ? (isUid ? maxUid : maxMsn) : int.Parse(startStr);
                    int end = endStr == "*" ? (isUid ? maxUid : maxMsn) : int.Parse(endStr);
                    if (start > end) (start, end) = (end, start);
                    for (int i = start; i <= end; i++)
                        result.Add(i);
                }
            }

            return result;
        }

        /// <summary>
        /// Parses FETCH data items — handles macro names and parenthesised lists.
        /// </summary>
        private static List<string> ParseFetchItems(string itemsStr)
        {
            itemsStr = itemsStr.Trim();

            // Macros
            if (itemsStr.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                return new List<string> { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE" };
            if (itemsStr.Equals("FAST", StringComparison.OrdinalIgnoreCase))
                return new List<string> { "FLAGS", "INTERNALDATE", "RFC822.SIZE" };
            if (itemsStr.Equals("FULL", StringComparison.OrdinalIgnoreCase))
                return new List<string> { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY" };

            // Strip outer parens
            if (itemsStr.StartsWith("(") && itemsStr.EndsWith(")"))
                itemsStr = itemsStr[1..^1].Trim();

            // Split on spaces but keep BODY[...] as a single token
            var items = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            foreach (char c in itemsStr)
            {
                if (c == '[') depth++;
                else if (c == ']') depth--;

                if (c == ' ' && depth == 0)
                {
                    if (current.Length > 0) { items.Add(current.ToString()); current.Clear(); }
                }
                else
                {
                    current.Append(c);
                }
            }
            if (current.Length > 0) items.Add(current.ToString());
            return items;
        }

        /// <summary>
        /// Resolves a MIME part by IMAP section number (e.g. "1", "1.1", "2").
        /// </summary>
        private static MimeKit.MimeEntity? ResolveMimePart(MimeKit.MimeEntity entity, string section)
        {
            // Strip .MIME suffix for resolution
            var cleanSection = section.EndsWith(".MIME")
                ? section.Substring(0, section.Length - 5)
                : section;

            var parts = cleanSection.Split('.');
            var current = entity;

            foreach (var partStr in parts)
            {
                if (!int.TryParse(partStr, out int partNum) || partNum < 1)
                    return null;

                if (current is MimeKit.Multipart multipart)
                {
                    if (partNum > multipart.Count) return null;
                    current = multipart[partNum - 1];
                }
                else
                {
                    // Non-multipart: only part 1 is valid (the entity itself)
                    if (partNum == 1)
                        return current;
                    return null;
                }
            }

            return current;
        }

        /// <summary>
        /// Parses two whitespace/quoted-string arguments (e.g., LOGIN "user" "pass").
        /// </summary>
        private static (string, string) ParseTwoArgs(string args)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false;

            for (int i = 0; i < args.Length; i++)
            {
                char c = args[i];
                if (c == '"') { inQuote = !inQuote; continue; }
                if (c == ' ' && !inQuote)
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());

            return (tokens.ElementAtOrDefault(0) ?? "", tokens.ElementAtOrDefault(1) ?? "");
        }

        /// <summary>
        /// Extracts a mailbox name from the beginning of an args string (handles quoted names).
        /// </summary>
        private static string ExtractMailboxName(string args, out string remaining)
        {
            args = args.Trim();
            if (args.StartsWith("\""))
            {
                var end = args.IndexOf('"', 1);
                if (end > 0)
                {
                    remaining = args.Substring(end + 1).TrimStart();
                    return args.Substring(1, end - 1);
                }
            }
            var spaceIdx = args.IndexOf(' ');
            if (spaceIdx < 0) { remaining = ""; return args; }
            remaining = args.Substring(spaceIdx + 1).TrimStart();
            return args.Substring(0, spaceIdx);
        }

        /// <summary>
        /// Encodes a mailbox name, escaping characters that need escaping in IMAP.
        /// </summary>
        private static string EncodeMailboxName(string name)
        {
            return name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Parses the header-field list from a section spec like "HEADER.FIELDS (From To Subject Date)"
        /// or "HEADER.FIELDS.NOT (Received X-Spam-Status)" into a list of field names.
        /// </summary>
        private static List<string> ParseHeaderFieldList(string section)
        {
            var openParen = section.IndexOf('(');
            var closeParen = section.LastIndexOf(')');
            if (openParen < 0 || closeParen <= openParen) return new List<string>();
            var inside = section.Substring(openParen + 1, closeParen - openParen - 1);
            return inside
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('"'))
                .Where(s => s.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Maps a folder name to its RFC 6154 special-use attribute, or null for plain folders.
        /// Apple Mail relies on these to render icons, route Sent/Trash/Drafts correctly, and
        /// suggest the archive folder for the Archive gesture.
        /// </summary>
        private static string? DetectSpecialUseFlag(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName)) return null;
            var lower = folderName.ToLowerInvariant();

            if (lower == "sent" || lower.Contains("sent items") || lower.Contains("sent messages") || lower.EndsWith("/sent"))
                return @"\Sent";
            if (lower == "trash" || lower == "deleted items" || lower == "deleted messages" || lower == "bin" || lower.EndsWith("/trash"))
                return @"\Trash";
            if (lower == "drafts" || lower.EndsWith("/drafts"))
                return @"\Drafts";
            if (lower == "junk" || lower == "spam" || lower == "junk e-mail" || lower.EndsWith("/junk") || lower.EndsWith("/spam"))
                return @"\Junk";
            if (lower == "archive" || lower.Contains("all mail") || lower.EndsWith("/archive"))
                return @"\Archive";
            return null;
        }

        /// <summary>
        /// Returns the assembly version, reported in the ID response so clients can log which
        /// server they're talking to.
        /// </summary>
        private static string GetAssemblyVersion()
        {
            var asm = typeof(ImapSession).Assembly;
            var version = asm.GetName().Version;
            return version != null ? version.ToString(3) : "0.0.0";
        }

        /// <summary>
        /// IDLE (RFC 2177): client enters IDLE, we send untagged responses as state changes, and the
        /// session exits IDLE when the client sends "DONE". Since the archive is read-only we never
        /// have unsolicited updates to push — we just hold the connection and wait for DONE.
        /// RFC recommends a server timeout of no more than 29 minutes to avoid NAT/proxy drops.
        /// </summary>
        private async Task HandleIdleAsync(string tag, StreamReader reader, StreamWriter writer, CancellationToken ct)
        {
            _logger.LogInformation("IDLE entered folder={Folder}", _selectedFolder ?? "<none>");
            await writer.WriteLineAsync("+ idling");
            await writer.FlushAsync(ct);

            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(TimeSpan.FromMinutes(29));

            try
            {
                while (!idleCts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(idleCts.Token);
                    if (line == null)
                    {
                        _logger.LogInformation("IDLE exited reason=client-disconnect folder={Folder}", _selectedFolder ?? "<none>");
                        return;
                    }
                    if (line.Trim().Equals("DONE", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync($"{tag} OK IDLE terminated");
                        _logger.LogInformation("IDLE exited reason=done folder={Folder}", _selectedFolder ?? "<none>");
                        return;
                    }
                    // Ignore any other input during IDLE (client shouldn't send anything else).
                }

                // 29-minute window expired — close the IDLE gracefully so the client reconnects.
                await writer.WriteLineAsync($"{tag} OK IDLE terminated by server timeout");
                _logger.LogInformation("IDLE exited reason=timeout folder={Folder}", _selectedFolder ?? "<none>");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                await writer.WriteLineAsync($"{tag} OK IDLE terminated by server timeout");
                _logger.LogInformation("IDLE exited reason=timeout folder={Folder}", _selectedFolder ?? "<none>");
            }
        }
    }
}
