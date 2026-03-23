using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
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

        private readonly TcpClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger _logger;

        private SessionState _state = SessionState.NotAuthenticated;
        private MailAccount? _account;
        private string? _selectedFolder;
        private List<ArchivedEmail> _folderMessages = new();

        public ImapSession(TcpClient client, IServiceScopeFactory scopeFactory, ILogger logger)
        {
            _client = client;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task HandleAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

            using var stream = _client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = false
            };

            var remote = _client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _logger.LogInformation("IMAP client connected from {Remote}", remote);

            try
            {
                await writer.WriteLineAsync("* OK MailArchiver IMAP4rev1 Server Ready");
                await writer.FlushAsync(ct);

                while (!ct.IsCancellationRequested && _state != SessionState.Logout)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break; // client disconnected

                    _logger.LogDebug("IMAP << {Line}", line);

                    await ProcessCommandAsync(line, reader, writer, db, ct);
                    await writer.FlushAsync(ct);
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
                _client.Close();
                _logger.LogInformation("IMAP client disconnected from {Remote}", remote);
            }
        }

        // -----------------------------------------------------------------------
        // Command dispatch
        // -----------------------------------------------------------------------

        private async Task ProcessCommandAsync(string line, StreamReader reader, StreamWriter writer,
            MailArchiverDbContext db, CancellationToken ct)
        {
            // Parse: TAG SP COMMAND [SP ARGS]
            var parts = line.Split(' ', 3);
            if (parts.Length < 2)
            {
                await writer.WriteLineAsync("* BAD Invalid command");
                return;
            }

            var tag = parts[0];
            var command = parts[1].ToUpperInvariant();
            var args = parts.Length > 2 ? parts[2] : "";

            _logger.LogDebug("IMAP tag={Tag} cmd={Command}", tag, command);

            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 LITERAL+");
                    await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                    break;

                case "NOOP":
                    await writer.WriteLineAsync($"{tag} OK NOOP completed");
                    break;

                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE MailArchiver IMAP4rev1 Server logging out");
                    await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                    _state = SessionState.Logout;
                    break;

                case "LOGIN":
                    await HandleLoginAsync(tag, args, writer, db, ct);
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
                case "COPY":
                    await writer.WriteLineAsync($"{tag} NO [{command}] Read-only mailbox");
                    break;

                default:
                    await writer.WriteLineAsync($"{tag} BAD Unknown command {command}");
                    break;
            }
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

            var account = await db.MailAccounts
                .FirstOrDefaultAsync(a => a.EmailAddress == user && a.ImapPassword != null && a.ImapPassword == pass, ct);

            if (account == null)
            {
                _logger.LogWarning("IMAP LOGIN failed for user {User}", user);
                await writer.WriteLineAsync($"{tag} NO LOGIN failed");
                return;
            }

            _account = account;
            _state = SessionState.Authenticated;
            _logger.LogInformation("IMAP LOGIN succeeded for {User} (account: {AccountName})", user, account.Name);
            await writer.WriteLineAsync($"{tag} OK LOGIN completed");
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
                await writer.WriteLineAsync($"* LIST (\\HasNoChildren) \"/\" \"{encodedFolder}\"");
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

            await writer.WriteLineAsync($"* STATUS \"{EncodeMailboxName(folder)}\" (MESSAGES {count} RECENT 0 UIDNEXT {maxId + 1} UIDVALIDITY 1 UNSEEN 0)");
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

            await writer.WriteLineAsync($"* {count} EXISTS");
            await writer.WriteLineAsync("* 0 RECENT");
            await writer.WriteLineAsync("* OK [UIDVALIDITY 1] UIDs valid");
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
                case "COPY":
                    await writer.WriteLineAsync($"{tag} NO [READ-ONLY] Read-only mailbox");
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

            for (int i = 0; i < _folderMessages.Count; i++)
            {
                var email = _folderMessages[i];
                bool match = true;

                if (upperArgs == "ALL" || upperArgs == "")
                {
                    match = true;
                }
                else if (upperArgs.StartsWith("SINCE "))
                {
                    var dateStr = args.Substring(6).Trim().Trim('"');
                    if (DateTime.TryParse(dateStr, out var since))
                        match = email.SentDate >= since;
                }
                else if (upperArgs.StartsWith("BEFORE "))
                {
                    var dateStr = args.Substring(7).Trim().Trim('"');
                    if (DateTime.TryParse(dateStr, out var before))
                        match = email.SentDate < before;
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

            await writer.WriteLineAsync($"{tag} OK FETCH completed");
        }

        private async Task WriteFetchResponseAsync(int msn, ArchivedEmail email, string itemsStr,
            StreamWriter writer, CancellationToken ct)
        {
            var items = ParseFetchItems(itemsStr);
            var nonLiteralParts = new List<string>();
            byte[]? literalBytes = null;
            string? literalItemName = null;

            foreach (var item in items)
            {
                var upper = item.ToUpperInvariant();

                if (upper == "UID")
                    nonLiteralParts.Add($"UID {email.Id}");
                else if (upper == "FLAGS")
                    nonLiteralParts.Add(@"FLAGS (\Seen)");
                else if (upper == "INTERNALDATE")
                    nonLiteralParts.Add($"INTERNALDATE \"{email.ReceivedDate.ToString("dd-MMM-yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture)}\"");
                else if (upper == "RFC822.SIZE")
                    nonLiteralParts.Add($"RFC822.SIZE {ImapMessageBuilder.GetMessageSize(email)}");
                else if (upper == "ENVELOPE")
                    nonLiteralParts.Add($"ENVELOPE {ImapMessageBuilder.BuildEnvelope(email)}");
                else if (upper == "BODYSTRUCTURE" || upper == "BODY")
                    nonLiteralParts.Add($"{upper} {ImapMessageBuilder.BuildBodyStructure(email)}");
                else if (upper == "RFC822" || upper == "BODY[]" || upper == "BODY.PEEK[]")
                {
                    var msg = ImapMessageBuilder.BuildMessage(email);
                    literalBytes = ImapMessageBuilder.SerializeMessage(msg);
                    literalItemName = upper == "RFC822" ? "RFC822" : "BODY[]";
                }
                else if (upper.StartsWith("BODY[HEADER") || upper.StartsWith("BODY.PEEK[HEADER"))
                {
                    var msg = ImapMessageBuilder.BuildMessage(email);
                    using var ms = new MemoryStream();
                    msg.Headers.WriteTo(ms);
                    var crlf = Encoding.UTF8.GetBytes("\r\n");
                    ms.Write(crlf, 0, crlf.Length);
                    literalBytes = ms.ToArray();
                    literalItemName = "BODY[HEADER]";
                }
                else if (upper.StartsWith("BODY[TEXT") || upper.StartsWith("BODY.PEEK[TEXT"))
                {
                    literalBytes = Encoding.UTF8.GetBytes(email.Body ?? "");
                    literalItemName = "BODY[TEXT]";
                }
            }

            var stream = writer.BaseStream;

            if (literalBytes != null && literalItemName != null)
            {
                // RFC 3501 literal response: "* N FETCH (items ITEMNAME {size}\r\nBYTES\r\n)\r\n"
                var sep = nonLiteralParts.Count > 0 ? " " : "";
                var prefix = $"* {msn} FETCH ({string.Join(" ", nonLiteralParts)}{sep}{literalItemName} {{{literalBytes.Length}}}\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(prefix), ct);
                await stream.WriteAsync(literalBytes, ct);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(")\r\n"), ct);
                await stream.FlushAsync(ct);
            }
            else
            {
                await writer.WriteLineAsync($"* {msn} FETCH ({string.Join(" ", nonLiteralParts)})");
            }
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
    }
}
