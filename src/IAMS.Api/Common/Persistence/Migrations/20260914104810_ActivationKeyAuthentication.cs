using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivationKeyAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OtpChallenges");

            migrationBuilder.DropTable(
                name: "UserDeviceBindings");

            migrationBuilder.DropTable(
                name: "UserTwoFactorSettings");

            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsTwoFactorComplete",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "NormalizedUsername",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Users");

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivatedAtUtc",
                table: "Users",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivatedDeviceId",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Added NULLABLE first, deliberately — see the backfill immediately below for why (CRITICAL
            // fix from code review: the original version of this migration added this column NOT NULL
            // with a single shared literal default of "", which (a) fails the CK_Users_ActivationStatus
            // check constraint added further down for every pre-existing row on ANY database that has
            // ever run the old password/2FA schema, and (b) would have collided on the UNIQUE index below
            // for any table with more than one pre-existing row, since every row would share that same ""
            // value. Both are now avoided by backfilling BEFORE tightening to NOT NULL / adding the
            // constraints, instead of relying on ADD COLUMN's one-shot default to do it correctly.
            migrationBuilder.AddColumn<string>(
                name: "ActivationKeyHash",
                table: "Users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivationResetAtUtc",
                table: "Users",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivationResetByUserId",
                table: "Users",
                type: "uuid",
                nullable: true);

            // Also added NULLABLE first — every pre-existing row backfills to the SAME literal value
            // ('NotActivated'), so there is no uniqueness concern here (unlike ActivationKeyHash above),
            // but the backfill must still land before CK_Users_ActivationStatus is added below, or the
            // check runs against still-null/still-"" rows and fails exactly as code review reproduced.
            migrationBuilder.AddColumn<string>(
                name: "ActivationStatus",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Users",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // Backfill EVERY pre-existing row (there is none on a genuinely fresh/greenfield database,
            // but this migration must also be safe on a dev/staging/demo database that has ever exercised
            // the old password/2FA system — exactly the scenario code review reproduced).
            //
            // ActivationKeyHash: no real Activation Key has ever been issued to a row created under the
            // old schema, so there is nothing meaningful to backfill it WITH — but the column is UNIQUE
            // and NOT NULL, so every such row needs SOME distinct placeholder. Each row's own Id is
            // already unique, and a "legacy:<guid>" string can never collide with, or be mistaken for, a
            // genuine SHA-256 hex hash (which is exactly 64 lowercase hex characters — a different shape
            // entirely). These rows are effectively "no Activation Key issued yet" until an admin
            // provisions a real one directly in the database, same as IsSystemAdmin today.
            //
            // ActivationStatus: every such row becomes NotActivated — there is only one correct value for
            // ALL of them, so a plain UPDATE (not a per-row-unique one) is enough.
            migrationBuilder.Sql(
                "UPDATE \"Users\" SET " +
                "\"ActivationKeyHash\" = 'legacy:' || \"Id\"::text, " +
                "\"ActivationStatus\" = 'NotActivated' " +
                "WHERE \"ActivationKeyHash\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "ActivationKeyHash",
                table: "Users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ActivationStatus",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ActivationKeyHash",
                table: "Users",
                column: "ActivationKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ActivationResetByUserId",
                table: "Users",
                column: "ActivationResetByUserId");

            // Added AFTER the backfill above, not alongside the column — Postgres validates a plain CHECK
            // constraint against every existing row at ADD CONSTRAINT time, so this must run only once
            // every row already holds a valid value.
            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_ActivationStatus",
                table: "Users",
                sql: "\"ActivationStatus\" IN ('NotActivated', 'Activated')");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ActivationResetByUserId",
                table: "Users",
                column: "ActivationResetByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ActivationResetByUserId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_ActivationKeyHash",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_ActivationResetByUserId",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_ActivationStatus",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivatedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivatedDeviceId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivationKeyHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivationResetAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivationResetByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ActivationStatus",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Users");

            migrationBuilder.AddColumn<bool>(
                name: "IsTwoFactorComplete",
                table: "UserSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Added NULLABLE first, same reason as ActivationKeyHash in Up() above: this column is about
            // to get a UNIQUE index back (IX_Users_NormalizedUsername, below), so a single shared literal
            // default ("") would collide across every pre-existing row on any table with more than one —
            // QA reproduced exactly this on a 3-legacy-row DB (Postgres 23505). The original data was lost
            // when Up() dropped this column, so there is nothing real to restore it to; each row instead
            // gets a placeholder derived from its own (already-unique) Id, mirroring the Up()-side
            // 'legacy:<Id>' convention for ActivationKeyHash.
            migrationBuilder.AddColumn<string>(
                name: "NormalizedUsername",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            // Backfill before tightening NormalizedUsername to NOT NULL / recreating its unique index
            // below — same ordering fix as Up(). PasswordHash has no uniqueness constraint, so its single
            // shared "" default (added above) is safe as-is.
            migrationBuilder.Sql(
                "UPDATE \"Users\" SET \"NormalizedUsername\" = 'LEGACY:' || \"Id\"::text " +
                "WHERE \"NormalizedUsername\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedUsername",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "OtpChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ChallengeToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResendAvailableAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ResendCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpChallenges", x => x.Id);
                    table.CheckConstraint("CK_OtpChallenges_Channel", "\"Channel\" IN ('Email', 'Sms', 'Authenticator')");
                    table.CheckConstraint("CK_OtpChallenges_Purpose", "\"Purpose\" IN ('Login')");
                    table.ForeignKey(
                        name: "FK_OtpChallenges_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserDeviceBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResetByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DeviceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastAuthenticatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    RegisteredAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    ResetAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDeviceBindings", x => x.Id);
                    table.CheckConstraint("CK_UserDeviceBindings_Status", "\"Status\" IN ('Active', 'Reset')");
                    table.ForeignKey(
                        name: "FK_UserDeviceBindings_Users_ResetByUserId",
                        column: x => x.ResetByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserDeviceBindings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTwoFactorSettings",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SharedSecret = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTwoFactorSettings", x => x.UserId);
                    table.CheckConstraint("CK_UserTwoFactorSettings_Channel", "\"Channel\" IN ('Email', 'Sms', 'Authenticator')");
                    table.ForeignKey(
                        name: "FK_UserTwoFactorSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_ChallengeToken",
                table: "OtpChallenges",
                column: "ChallengeToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_UserId_Purpose",
                table: "OtpChallenges",
                columns: new[] { "UserId", "Purpose" },
                filter: "\"ConsumedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserDeviceBindings_ResetByUserId",
                table: "UserDeviceBindings",
                column: "ResetByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDeviceBindings_UserId",
                table: "UserDeviceBindings",
                column: "UserId",
                unique: true);
        }
    }
}
