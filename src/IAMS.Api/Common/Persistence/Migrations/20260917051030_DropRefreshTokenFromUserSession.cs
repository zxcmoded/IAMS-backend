using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropRefreshTokenFromUserSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_RefreshTokenHash",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "RefreshTokenHash",
                table: "UserSessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefreshTokenHash",
                table: "UserSessions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_RefreshTokenHash",
                table: "UserSessions",
                column: "RefreshTokenHash");
        }
    }
}
