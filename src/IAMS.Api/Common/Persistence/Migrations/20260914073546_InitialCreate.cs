using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAMS.Api.Common.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
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
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.CheckConstraint("CK_Tenants_Kind", "\"Kind\" IN ('Parent', 'Child')");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    IsSystemAdmin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Companies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OtpChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ResendAvailableAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
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
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeviceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RegisteredAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    LastAuthenticatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResetAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ResetByUserId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
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

            migrationBuilder.CreateTable(
                name: "CompanyConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PermissionLevel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
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
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Region = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: true),
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
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveCompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsTwoFactorComplete = table.Column<bool>(type: "boolean", nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RefreshTokenHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_Companies_ActiveCompanyId",
                        column: x => x.ActiveCompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSessions_Locations_ActiveLocationId",
                        column: x => x.ActiveLocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Warehouses_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Racks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Racks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Racks_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RackId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bins_Racks_RackId",
                        column: x => x.RackId,
                        principalTable: "Racks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyConnectionScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScopeCompanyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeWarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeRackId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScopeBinId = table.Column<Guid>(type: "uuid", nullable: true),
                    PermissionLevelOverride = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_Bins_CompanyId",
                table: "Bins",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_RackId",
                table: "Bins",
                column: "RackId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_TenantId",
                table: "Bins",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_WarehouseId",
                table: "Bins",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId",
                table: "Companies",
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
                name: "IX_Locations_CompanyId",
                table: "Locations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_CompanyId_Region",
                table: "Locations",
                columns: new[] { "CompanyId", "Region" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_TenantId",
                table: "Locations",
                column: "TenantId");

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
                name: "IX_Racks_CompanyId",
                table: "Racks",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Racks_TenantId",
                table: "Racks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Racks_WarehouseId",
                table: "Racks",
                column: "WarehouseId");

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

            migrationBuilder.CreateIndex(
                name: "IX_UserDeviceBindings_ResetByUserId",
                table: "UserDeviceBindings",
                column: "ResetByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDeviceBindings_UserId",
                table: "UserDeviceBindings",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUsername",
                table: "Users",
                column: "NormalizedUsername",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ActiveCompanyId",
                table: "UserSessions",
                column: "ActiveCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ActiveLocationId",
                table: "UserSessions",
                column: "ActiveLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_RefreshTokenHash",
                table: "UserSessions",
                column: "RefreshTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                table: "UserSessions",
                column: "UserId",
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_CompanyId",
                table: "Warehouses",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_LocationId",
                table: "Warehouses",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_TenantId",
                table: "Warehouses",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyConnectionFilters");

            migrationBuilder.DropTable(
                name: "CompanyConnectionScopes");

            migrationBuilder.DropTable(
                name: "OtpChallenges");

            migrationBuilder.DropTable(
                name: "UserCompanyMemberships");

            migrationBuilder.DropTable(
                name: "UserDeviceBindings");

            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "UserTwoFactorSettings");

            migrationBuilder.DropTable(
                name: "Bins");

            migrationBuilder.DropTable(
                name: "CompanyConnections");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Racks");

            migrationBuilder.DropTable(
                name: "Warehouses");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
