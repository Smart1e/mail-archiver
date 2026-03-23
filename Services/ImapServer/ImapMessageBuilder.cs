using MailArchiver.Models;
using MimeKit;
using MimeKit.Text;
using System.Text;

namespace MailArchiver.Services.ImapServer
{
    /// <summary>
    /// Reconstructs MimeMessage objects from archived email records and generates
    /// RFC 3501-compliant IMAP response strings (ENVELOPE, BODYSTRUCTURE).
    /// </summary>
    public static class ImapMessageBuilder
    {
        /// <summary>
        /// Builds a MimeMessage from an ArchivedEmail record using stored fields and attachments.
        /// </summary>
        public static MimeMessage BuildMessage(ArchivedEmail email)
        {
            var message = new MimeMessage();

            // Message-ID
            if (!string.IsNullOrEmpty(email.MessageId))
                message.MessageId = email.MessageId.Trim('<', '>');

            // Date
            message.Date = new DateTimeOffset(email.SentDate, TimeSpan.Zero);

            // Subject
            message.Subject = email.Subject ?? "";

            // Address fields — wrap parse errors so a bad address never breaks IMAP
            TryAddAddresses(message.From, email.From);
            TryAddAddresses(message.To, email.To);
            TryAddAddresses(message.Cc, email.Cc);
            TryAddAddresses(message.Bcc, email.Bcc);

            // Body
            var bodyBuilder = new BodyBuilder();

            // Prefer OriginalBodyText/Html (null-byte-safe versions) when available
            var textBody = email.OriginalBodyText != null
                ? Encoding.UTF8.GetString(email.OriginalBodyText)
                : email.Body;

            var htmlBody = email.OriginalBodyHtml != null
                ? Encoding.UTF8.GetString(email.OriginalBodyHtml)
                : email.HtmlBody;

            if (!string.IsNullOrEmpty(textBody))
                bodyBuilder.TextBody = textBody;
            if (!string.IsNullOrEmpty(htmlBody))
                bodyBuilder.HtmlBody = htmlBody;

            // Attachments
            foreach (var att in email.Attachments)
            {
                try
                {
                    ContentType? ct = null;
                    try { ct = ContentType.Parse(att.ContentType); } catch { }

                    var mimePart = ct != null
                        ? new MimePart(ct) { Content = new MimeContent(new MemoryStream(att.Content)) }
                        : new MimePart("application", "octet-stream") { Content = new MimeContent(new MemoryStream(att.Content)) };

                    mimePart.FileName = att.FileName;
                    mimePart.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment);
                    if (!string.IsNullOrEmpty(att.ContentId))
                        mimePart.ContentId = att.ContentId;

                    bodyBuilder.Attachments.Add(mimePart);
                }
                catch
                {
                    // Skip unparseable attachment rather than breaking the message
                }
            }

            message.Body = bodyBuilder.ToMessageBody();
            return message;
        }

        /// <summary>
        /// Serializes a MimeMessage to bytes.
        /// </summary>
        public static byte[] SerializeMessage(MimeMessage message)
        {
            using var ms = new MemoryStream();
            message.WriteTo(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Returns an estimated byte size of the serialized message without fully constructing it.
        /// </summary>
        public static int GetMessageSize(ArchivedEmail email)
        {
            // Rough estimation: headers + body + attachments
            var headerSize = 500; // approximate fixed header overhead
            var bodySize = (email.Body?.Length ?? 0) + (email.HtmlBody?.Length ?? 0);
            var attachSize = email.Attachments.Sum(a => (int)a.Size);
            // Base64 encoding adds ~33% overhead for attachments
            return headerSize + bodySize + (int)(attachSize * 1.34);
        }

        /// <summary>
        /// Builds an RFC 3501 ENVELOPE string for use in IMAP FETCH responses.
        /// Format: ("date" "subject" from sender reply-to to cc bcc in-reply-to message-id)
        /// </summary>
        public static string BuildEnvelope(ArchivedEmail email)
        {
            var date = FormatEnvelopeDate(email.SentDate);
            var subject = EncodeNilOrString(email.Subject);
            var from = BuildAddressList(email.From);
            var to = BuildAddressList(email.To);
            var cc = BuildAddressList(email.Cc);
            var bcc = BuildAddressList(email.Bcc);
            var msgId = EncodeNilOrString(string.IsNullOrEmpty(email.MessageId) ? null : $"<{email.MessageId.Trim('<', '>')}>");

            // sender and reply-to default to from; in-reply-to is NIL
            return $"({date} {subject} {from} {from} {from} {to} {cc} {bcc} NIL {msgId})";
        }

        /// <summary>
        /// Builds an RFC 3501 BODYSTRUCTURE string for use in IMAP FETCH responses.
        /// </summary>
        public static string BuildBodyStructure(ArchivedEmail email)
        {
            bool hasText = !string.IsNullOrEmpty(email.Body ?? (email.OriginalBodyText != null ? "x" : null));
            bool hasHtml = !string.IsNullOrEmpty(email.HtmlBody ?? (email.OriginalBodyHtml != null ? "x" : null));
            bool hasAttachments = email.Attachments.Any();

            if (!hasText && !hasHtml)
            {
                // Minimal text/plain placeholder
                return "(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 0 0)";
            }

            if (!hasHtml && !hasAttachments)
            {
                // Simple text/plain
                var size = (email.Body?.Length ?? 0);
                var lines = CountLines(email.Body);
                return $"(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" {size} {lines})";
            }

            // Build multipart structure
            var parts = new List<string>();

            if (hasText)
            {
                var size = (email.Body?.Length ?? 0);
                var lines = CountLines(email.Body);
                parts.Add($"(\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" {size} {lines})");
            }

            if (hasHtml)
            {
                var size = (email.HtmlBody?.Length ?? 0);
                var lines = CountLines(email.HtmlBody);
                parts.Add($"(\"TEXT\" \"HTML\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" {size} {lines})");
            }

            if (hasAttachments)
            {
                foreach (var att in email.Attachments)
                {
                    var attParts = (att.ContentType ?? "application/octet-stream").Split('/', 2);
                    var attType = attParts.Length > 0 ? attParts[0].ToUpperInvariant() : "APPLICATION";
                    var attSubtype = attParts.Length > 1 ? attParts[1].ToUpperInvariant() : "OCTET-STREAM";
                    var attName = EncodeNilOrString(att.FileName);
                    parts.Add($"(\"{attType}\" \"{attSubtype}\" (\"NAME\" {attName}) NIL NIL \"BASE64\" {att.Size})");
                }

                // Wrap text + html as alternative, then mixed with attachments
                if (hasText && hasHtml)
                {
                    var altParts = string.Join(" ", parts.Take(2));
                    var mixedBody = $"({altParts} \"ALTERNATIVE\" NIL NIL NIL)";
                    var attachParts = string.Join(" ", parts.Skip(2));
                    return $"({mixedBody} {attachParts} \"MIXED\" NIL NIL NIL)";
                }

                return $"({string.Join(" ", parts)} \"MIXED\" NIL NIL NIL)";
            }

            if (hasText && hasHtml)
                return $"({string.Join(" ", parts)} \"ALTERNATIVE\" NIL NIL NIL)";

            return parts[0];
        }

        // --- Private helpers ---

        private static void TryAddAddresses(InternetAddressList list, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            try
            {
                list.AddRange(InternetAddressList.Parse(raw));
            }
            catch
            {
                // Fall back to treating the raw string as a display name with no email
                try { list.Add(new MailboxAddress(raw, "")); } catch { }
            }
        }

        private static string FormatEnvelopeDate(DateTime dt)
        {
            // RFC 2822 date format for ENVELOPE
            return $"\"{dt.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture)}\"";
        }

        private static string EncodeNilOrString(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "NIL";
            // Escape backslash and double-quote
            var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }

        /// <summary>
        /// Builds an RFC 3501 address list: ((name adl mailbox host) ...) or NIL.
        /// </summary>
        private static string BuildAddressList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "NIL";
            try
            {
                var addresses = InternetAddressList.Parse(raw);
                if (addresses.Count == 0) return "NIL";

                var sb = new StringBuilder("(");
                foreach (var addr in addresses)
                {
                    if (addr is MailboxAddress mailbox)
                    {
                        var name = EncodeNilOrString(mailbox.Name);
                        var localPart = EncodeNilOrString(mailbox.Address.Split('@').ElementAtOrDefault(0));
                        var domain = EncodeNilOrString(mailbox.Address.Split('@').ElementAtOrDefault(1));
                        sb.Append($"({name} NIL {localPart} {domain})");
                    }
                }
                sb.Append(')');
                return sb.Length > 2 ? sb.ToString() : "NIL";
            }
            catch
            {
                return "NIL";
            }
        }

        private static int CountLines(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Count(c => c == '\n') + 1;
        }
    }
}
