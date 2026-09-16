using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MasterDataSyncCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "Companies",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCursorUtc",
                table: "Warehouses",
                type: "timestamptz",
                nullable: false,
                computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")",
                stored: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCursorUtc",
                table: "Racks",
                type: "timestamptz",
                nullable: false,
                computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")",
                stored: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCursorUtc",
                table: "Locations",
                type: "timestamptz",
                nullable: false,
                computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")",
                stored: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCursorUtc",
                table: "Companies",
                type: "timestamptz",
                nullable: false,
                computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")",
                stored: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncCursorUtc",
                table: "Bins",
                type: "timestamptz",
                nullable: false,
                computedColumnSql: "COALESCE(\"UpdatedAtUtc\", \"CreatedAtUtc\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_Sync",
                table: "Warehouses",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Racks_Sync",
                table: "Racks",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Sync",
                table: "Locations",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Sync",
                table: "Companies",
                columns: new[] { "SyncCursorUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Bins_Sync",
                table: "Bins",
                columns: new[] { "CompanyId", "SyncCursorUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Warehouses_Sync",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_Racks_Sync",
                table: "Racks");

            migrationBuilder.DropIndex(
                name: "IX_Locations_Sync",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Companies_Sync",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Bins_Sync",
                table: "Bins");

            migrationBuilder.DropColumn(
                name: "SyncCursorUtc",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "SyncCursorUtc",
                table: "Racks");

            migrationBuilder.DropColumn(
                name: "SyncCursorUtc",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "SyncCursorUtc",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "SyncCursorUtc",
                table: "Bins");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Companies");
        }
    }
}
