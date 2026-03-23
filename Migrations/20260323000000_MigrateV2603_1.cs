using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2603_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add MinEmailAgeDays column to MailAccounts table
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'MinEmailAgeDays'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN ""MinEmailAgeDays"" integer;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                COMMENT ON COLUMN mail_archiver.""MailAccounts"".""MinEmailAgeDays"" IS 'Minimum age in days for emails to be archived (null = archive all emails)';
            ");

            // Add ImapPassword column to MailAccounts table
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'ImapPassword'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN ""ImapPassword"" text;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                COMMENT ON COLUMN mail_archiver.""MailAccounts"".""ImapPassword"" IS 'Password for built-in IMAP server access (null = IMAP access disabled for this account)';
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
                        AND column_name = 'MinEmailAgeDays'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" DROP COLUMN ""MinEmailAgeDays"";
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'ImapPassword'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" DROP COLUMN ""ImapPassword"";
                    END IF;
                END $$;
            ");
        }
    }
}
