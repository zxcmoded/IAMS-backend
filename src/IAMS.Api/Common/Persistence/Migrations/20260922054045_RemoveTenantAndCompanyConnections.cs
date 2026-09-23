using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTenantAndCompanyConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_Tenants_TenantId",
                table: "Companies");

            migrationBuilder.DropForeignKey(
                name: "FK_UserSessions_Companies_ActiveCompanyId",
                table: "UserSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_UserSessions_Locations_ActiveLocationId",
                table: "UserSessions");

            migrationBuilder.DropTable(
                name: "CompanyConnectionFilters");

            migrationBuilder.DropTable(
                name: "CompanyConnectionScopes");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "UserCompanyMemberships");

            migrationBuilder.DropTable(
                name: "CompanyConnections");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Warehouses_TenantId",
                table: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_ActiveCompanyId",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_ActiveLocationId",
                table: "UserSessions");

            migrationBuilder.DropIndex(
                name: "IX_StockLevels_TenantId",
                table: "StockLevels");

            migrationBuilder.DropIndex(
                name: "IX_StockCounts_TenantId",
                table: "StockCounts");

            migrationBuilder.DropIndex(
                name: "IX_ScanEvents_TenantId",
                table: "ScanEvents");

            migrationBuilder.DropIndex(
                name: "IX_Racks_TenantId",
                table: "Racks");

            migrationBuilder.DropIndex(
                name: "IX_Locations_TenantId",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_InventoryTransactions_TenantId",
                table: "InventoryTransactions");

            migrationBuilder.DropIndex(
                name: "IX_InventoryItems_TenantId",
                table: "InventoryItems");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId",
                table: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Bins_TenantId",
                table: "Bins");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "ActiveCompanyId",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "ActiveLocationId",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "IsSystemAdmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "StockLevels");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "StockCounts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ScanEvents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Racks");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "InventoryTransactions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "InventorySettings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "InventoryItems");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Bins");

            // Users.CompanyId is being introduced as NOT NULL with an FK to Companies. Any Users rows that
            // already exist (e.g. a seeded admin) need a real Companies row to point to before that FK is
            // added below - Guid.Empty is not a valid Companies.Id, so it would fail the FK add (or worse,
            // silently pass on an empty table and only blow up later). Bootstrap one deterministically so
            // the migration is safe whether Users is empty or already has rows.
            migrationBuilder.Sql(
                """
                INSERT INTO "Companies" ("Id", "Name", "IsActive")
                VALUES ('11111111-1111-1111-1111-111111111111', 'Unassigned', TRUE)
                ON CONFLICT ("Id") DO NOTHING;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Users",
                type: "integer",
                nullable: false,
                // Default is Viewer (100), the least-privileged valid code, so this column add stays inside
                // the CK_Users_Role domain even on a table that already has rows (defaultValue 0 would violate
                // the CHECK added just below). New rows always set Role explicitly via EF.
                defaultValue: 100);

            migrationBuilder.CreateTable(
                name: "UserLocationAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLocationAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserLocationAssignments_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserLocationAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_CompanyId",
                table: "Users",
                column: "CompanyId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Role",
                table: "Users",
                sql: "\"Role\" IN (100, 200, 300, 700, 800)");

            migrationBuilder.CreateIndex(
                name: "IX_UserLocationAssignments_LocationId",
                table: "UserLocationAssignments",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLocationAssignments_UserId_LocationId",
                table: "UserLocationAssignments",
                columns: new[] { "UserId", "LocationId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Companies_CompanyId",
                table: "Users",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Companies_CompanyId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "UserLocationAssignments");

            migrationBuilder.DropIndex(
                name: "IX_Users_CompanyId",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Role",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Warehouses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveCompanyId",
                table: "UserSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveLocationId",
                table: "UserSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemAdmin",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "StockLevels",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "StockCounts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ScanEvents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Racks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Locations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "InventoryTransactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "InventorySettings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "InventoryItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Companies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Bins",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "CompanyConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PermissionLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyConnections", x => x.Id);
                    table.CheckConstraint("CK_CompanyConnections_ConnectionType", "\"ConnectionType\" IN ('ParentToParent', 'ParentToChild', 'ChildToParent', 'ChildToChild')");
                    table.CheckConstraint("CK_CompanyConnections_NoSelf", "\"SourceCompanyId\" <> \"TargetCompanyId\"");
                    table.CheckConstraint("CK_CompanyConnections_PermissionLevel", "\"PermissionLevel\" IN ('Read', 'Write', 'Full')");
                    table.ForeignKey(
                        name: "FK_CompanyConnections_Companies_SourceCompanyId",
                        column: x => x.SourceCompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyConnections_Companies_TargetCompanyId",
                        column: x => x.TargetCompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.CheckConstraint("CK_Tenants_Kind", "\"Kind\" IN ('Parent', 'Child')");
                });

            migrationBuilder.CreateTable(
                name: "CompanyConnectionFilters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilterType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FilterValue = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyConnectionFilters", x => x.Id);
                    table.CheckConstraint("CK_CompanyConnectionFilters_FilterType", "\"FilterType\" IN ('Region', 'Location', 'Warehouse', 'Category')");
                    table.ForeignKey(
                        name: "FK_CompanyConnectionFilters_CompanyConnections_CompanyConnecti~",
                        column: x => x.CompanyConnectionId,
                        principalTable: "CompanyConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyConnectionScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PermissionLevelOverride = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ScopeBinId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeCompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeRackId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeWarehouseId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyConnectionScopes", x => x.Id);
                    table.CheckConstraint("CK_CompanyConnectionScopes_Level", "\"Level\" IN ('Company', 'Location', 'Warehouse', 'Rack', 'Bin')");
                    table.CheckConstraint("CK_ConnScope_LevelMatches", "(\"Level\" = 'Company'   AND \"ScopeCompanyId\"   IS NOT NULL) OR(\"Level\" = 'Location'  AND \"ScopeLocationId\"  IS NOT NULL) OR(\"Level\" = 'Warehouse' AND \"ScopeWarehouseId\" IS NOT NULL) OR(\"Level\" = 'Rack'      AND \"ScopeRackId\"      IS NOT NULL) OR(\"Level\" = 'Bin'       AND \"ScopeBinId\"       IS NOT NULL)");
                    table.CheckConstraint("CK_ConnScope_OneTarget", "(CASE WHEN \"ScopeCompanyId\"   IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"ScopeLocationId\"  IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"ScopeWarehouseId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"ScopeRackId\"      IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"ScopeBinId\"       IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.ForeignKey(
                        name: "FK_CompanyConnectionScopes_Bins_ScopeBinId",
                        column: x => x.ScopeBinId,
                        principalTable: "Bins",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompanyConnectionScopes_Companies_ScopeCompanyId",
                        column: x => x.ScopeCompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompanyConnectionScopes_CompanyConnections_CompanyConnectio~",
                        column: x => x.CompanyConnectionId,
                        principalTable: "CompanyConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyConnectionScopes_Locations_ScopeLocationId",
                        column: x => x.ScopeLocationId,
                        principalTable: "Locations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompanyConnectionScopes_Racks_ScopeRackId",
                        column: x => x.ScopeRackId,
                        principalTable: "Racks",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CompanyConnectionScopes_Warehouses_ScopeWarehouseId",
                        column: x => x.ScopeWarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCompanyMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserCompanyMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_TenantId",
                table: "Warehouses",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ActiveCompanyId",
                table: "UserSessions",
                column: "ActiveCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ActiveLocationId",
                table: "UserSessions",
                column: "ActiveLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_StockLevels_TenantId",
                table: "StockLevels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_TenantId",
                table: "StockCounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanEvents_TenantId",
                table: "ScanEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Racks_TenantId",
                table: "Racks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId",
                table: "Locations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryTransactions_TenantId",
                table: "InventoryTransactions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_TenantId",
                table: "InventoryItems",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId",
                table: "Companies",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_TenantId",
                table: "Bins",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionFilters_CompanyConnectionId",
                table: "CompanyConnectionFilters",
                column: "CompanyConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnections_SourceCompanyId_TargetCompanyId",
                table: "CompanyConnections",
                columns: new[] { "SourceCompanyId", "TargetCompanyId" },
                unique: true)
                .Annotation("Npgsql:IndexInclude", new[] { "IsEnabled", "PermissionLevel", "ConnectionType", "PolicyRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnections_TargetCompanyId",
                table: "CompanyConnections",
                column: "TargetCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionScopes_CompanyConnectionId",
                table: "CompanyConnectionScopes",
                column: "CompanyConnectionId")
                .Annotation("Npgsql:IndexInclude", new[] { "Level", "ScopeCompanyId", "ScopeLocationId", "ScopeWarehouseId", "ScopeRackId", "ScopeBinId", "PermissionLevelOverride" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionScopes_ScopeBinId",
                table: "CompanyConnectionScopes",
                column: "ScopeBinId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionScopes_ScopeCompanyId",
                table: "CompanyConnectionScopes",
                column: "ScopeCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionScopes_ScopeLocationId",
                table: "CompanyConnectionScopes",
                column: "ScopeLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionScopes_ScopeRackId",
                table: "CompanyConnectionScopes",
                column: "ScopeRackId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyConnectionScopes_ScopeWarehouseId",
                table: "CompanyConnectionScopes",
                column: "ScopeWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Kind",
                table: "Tenants",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_CompanyId",
                table: "UserCompanyMemberships",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_RoleId",
                table: "UserCompanyMemberships",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMemberships_UserId_CompanyId",
                table: "UserCompanyMemberships",
                columns: new[] { "UserId", "CompanyId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_Tenants_TenantId",
                table: "Companies",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSessions_Companies_ActiveCompanyId",
                table: "UserSessions",
                column: "ActiveCompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserSessions_Locations_ActiveLocationId",
                table: "UserSessions",
                column: "ActiveLocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
