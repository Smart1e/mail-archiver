using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2604_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'ArchivedEmails'
                        AND column_name = 'CachedRfc822Size'
                    ) THEN
                        ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN ""CachedRfc822Size"" integer;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                COMMENT ON COLUMN mail_archiver.""ArchivedEmails"".""CachedRfc822Size"" IS 'Cached RFC822.SIZE reported by the built-in IMAP server. Stable value prevents Apple Mail from re-downloading messages when reconstruction estimates drift.';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'ArchivedEmails'
                        AND column_name = 'CachedRfc822Size'
                    ) THEN
                        ALTER TABLE mail_archiver.""ArchivedEmails"" DROP COLUMN ""CachedRfc822Size"";
                    END IF;
                END $$;
            ");
        }
    }
}
