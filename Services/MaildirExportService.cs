using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.ImapServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services
{
    /// <summary>
    /// Projects the Postgres archive onto a Maildir tree that Dovecot serves. The archive DB is the
    /// source of truth; this service only writes RFC822-serialized copies to disk and deletes orphans
    /// when retention removes rows from the DB.
    ///
    /// Directory layout (LAYOUT=fs in Dovecot):
    ///   {Root}/{MailAccount.Id}/Maildir/{cur,new,tmp}            ← INBOX (root Maildir)
    ///   {Root}/{MailAccount.Id}/Maildir/{Sanitized}/{cur,new,tmp} ← other folders
    ///
    /// Filename: archive-{email.Id}.M{epoch}.mailarchiver,S={size}:2,S
    ///   - starts with "archive-{Id}." so we can idempotently detect "already exported?" by prefix
    ///   - S={size} lets Dovecot answer RFC822.SIZE without reading the file
    ///   - :2,S marks the message \Seen — archived mail is historical and a user can still toggle locally
    ///
    /// We never touch dovecot.index*, dovecot-uidlist, or any other Dovecot-owned file — those are
    /// load-bearing for UID/UIDVALIDITY stability across client reconnects.
    /// </summary>
    public class MaildirExportService : BackgroundService
    {
        private readonly MaildirOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MaildirExportService> _logger;

        public MaildirExportService(
            IOptions<MaildirOptions> options,
            IServiceScopeFactory scopeFactory,
            ILogger<MaildirExportService> logger)
        {
            _options = options.Value;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("MaildirExportService disabled (set Maildir:Enabled=true to enable)");
                return;
            }

            _logger.LogInformation("MaildirExportService starting — root={Root} tick={Tick}s", _options.Root, _options.TickSeconds);

            // Small initial delay so the DB is fully migrated before the first scan.
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunTickAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MaildirExportService tick failed");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(_options.TickSeconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RunTickAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

            var accounts = await db.MailAccounts
                .Where(a => a.IsEnabled && a.ImapPassword != null)
                .Select(a => a.Id)
                .ToListAsync(ct);

            foreach (var accountId in accounts)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await SyncAccountAsync(db, accountId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Maildir sync failed for account {AccountId}", accountId);
                }
            }
        }

        private async Task SyncAccountAsync(MailArchiverDbContext db, int accountId, CancellationToken ct)
        {
            // Per-folder: set of email.Id we expect on disk (from DB) vs. what's actually there.
            // Using one grouped query keeps the DB round-trip count low on large archives.
            var grouped = await db.ArchivedEmails
                .Where(e => e.MailAccountId == accountId)
                .Select(e => new { e.Id, e.FolderName })
                .ToListAsync(ct);

            var byFolder = grouped
                .GroupBy(x => NormalizeFolder(x.FolderName))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToHashSet());

            var accountRoot = Path.Combine(_options.Root, accountId.ToString(), "Maildir");
            Directory.CreateDirectory(accountRoot);
            // Scaffold INBOX even for empty accounts so Dovecot can SELECT INBOX successfully.
            Directory.CreateDirectory(Path.Combine(accountRoot, "cur"));
            Directory.CreateDirectory(Path.Combine(accountRoot, "new"));
            Directory.CreateDirectory(Path.Combine(accountRoot, "tmp"));

            int materialized = 0;
            int deleted = 0;

            foreach (var kv in byFolder)
            {
                if (ct.IsCancellationRequested) return;

                var folderPath = kv.Key == "INBOX"
                    ? accountRoot
                    : Path.Combine(accountRoot, kv.Key);
                var curDir = Path.Combine(folderPath, "cur");
                var newDir = Path.Combine(folderPath, "new");
                var tmpDir = Path.Combine(folderPath, "tmp");

                Directory.CreateDirectory(curDir);
                Directory.CreateDirectory(newDir);
                Directory.CreateDirectory(tmpDir);

                var existingIds = IndexExistingFiles(curDir);
                var wanted = kv.Value;

                // Materialize missing (batched so we don't load thousands of full MIME messages at once).
                var toMaterialize = wanted.Except(existingIds.Keys).Take(_options.BatchSize).ToList();
                foreach (var id in toMaterialize)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        await MaterializeAsync(db, id, tmpDir, curDir, ct);
                        materialized++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to materialize email {Id} into {Dir}", id, curDir);
                    }
                }

                // Orphan removal (retention deleted the DB row).
                foreach (var orphan in existingIds.Keys.Except(wanted))
                {
                    try
                    {
                        File.Delete(existingIds[orphan]);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphan Maildir file {Path}", existingIds[orphan]);
                    }
                }
            }

            if (materialized > 0 || deleted > 0)
            {
                _logger.LogInformation("Maildir sync account={AccountId} materialized={Added} deleted={Deleted}",
                    accountId, materialized, deleted);
            }
        }

        /// <summary>
        /// Builds a MimeMessage from the DB row, serializes it to CRLF RFC822 bytes, writes into
        /// tmp/ with the deterministic filename, then atomically renames into cur/. Maildir invariant:
        /// never publish a file to cur/ that isn't fully written.
        /// </summary>
        private async Task MaterializeAsync(MailArchiverDbContext db, int emailId, string tmpDir, string curDir, CancellationToken ct)
        {
            var email = await db.ArchivedEmails
                .Include(e => e.Attachments)
                .FirstOrDefaultAsync(e => e.Id == emailId, ct);
            if (email == null) return;

            var msg = ImapMessageBuilder.BuildMessage(email);
            var bytes = ImapMessageBuilder.SerializeMessage(msg);

            // Deterministic, prefix-addressable filename. The {epoch} and hostname chunk satisfy the
            // Maildir uniqueness convention without changing the prefix — still safe to look up by Id.
            var epoch = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds();
            var filename = $"archive-{email.Id}.M{epoch}.mailarchiver,S={bytes.Length}:2,S";

            var tmpPath = Path.Combine(tmpDir, filename);
            var curPath = Path.Combine(curDir, filename);

            await File.WriteAllBytesAsync(tmpPath, bytes, ct);
            // Atomic move into cur/ — Dovecot only scans cur/ and new/, so the file is invisible until
            // the rename completes.
            File.Move(tmpPath, curPath, overwrite: false);

            if (email.CachedRfc822Size == null || email.CachedRfc822Size != bytes.Length)
            {
                email.CachedRfc822Size = bytes.Length;
                await db.SaveChangesAsync(ct);
            }
        }

        /// <summary>
        /// Scans cur/ for files matching our naming prefix and returns a map of email.Id → full path.
        /// Anything else in the directory (Dovecot-managed files, unexpected stragglers) is ignored.
        /// </summary>
        private static Dictionary<int, string> IndexExistingFiles(string curDir)
        {
            var result = new Dictionary<int, string>();
            if (!Directory.Exists(curDir)) return result;

            foreach (var file in Directory.EnumerateFiles(curDir, "archive-*"))
            {
                var name = Path.GetFileName(file);
                // Expect "archive-<id>." at the start.
                if (!name.StartsWith("archive-", StringComparison.Ordinal)) continue;
                var rest = name.AsSpan("archive-".Length);
                var dot = rest.IndexOf('.');
                if (dot <= 0) continue;
                if (!int.TryParse(rest.Slice(0, dot), out var id)) continue;
                result[id] = file;
            }
            return result;
        }

        /// <summary>
        /// Maps an ArchivedEmail.FolderName to a filesystem-safe subfolder name. INBOX is special-cased
        /// (lives at the Maildir root); everything else is sanitized to strip characters that would
        /// confuse Dovecot's LAYOUT=fs resolver (path separators, leading dots).
        /// </summary>
        private static string NormalizeFolder(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "INBOX";
            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "INBOX", StringComparison.OrdinalIgnoreCase)) return "INBOX";

            // Replace path separators with underscores so "Archive/2024" becomes "Archive_2024".
            // Keeping it flat avoids having to pre-create the whole hierarchy.
            var sanitized = trimmed
                .Replace('/', '_')
                .Replace('\\', '_')
                .Replace('\0', '_')
                .TrimStart('.');
            return string.IsNullOrEmpty(sanitized) ? "INBOX" : sanitized;
        }
    }
}
