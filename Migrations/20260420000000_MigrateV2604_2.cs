using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2604_2 : Migration
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
                        AND table_name = 'MailAccounts'
                        AND column_name = 'ArchiveImapUsername'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN ""ArchiveImapUsername"" text;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                COMMENT ON COLUMN mail_archiver.""MailAccounts"".""ArchiveImapUsername"" IS 'Optional override: address clients use to log in to the built-in IMAP server. When set, the archive server matches on this instead of EmailAddress, so you can expose a different email to clients than the real sync address.';
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
                        AND table_name = 'MailAccounts'
                        AND column_name = 'ArchiveImapUsername'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" DROP COLUMN ""ArchiveImapUsername"";
                    END IF;
                END $$;
            ");
        }
    }
}
