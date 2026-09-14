CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE "Roles" (
    "Id" uuid NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Description" character varying(400),
    CONSTRAINT "PK_Roles" PRIMARY KEY ("Id")
);

CREATE TABLE "Tenants" (
    "Id" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "Kind" character varying(20) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_Tenants_Kind" CHECK ("Kind" IN ('Parent', 'Child'))
);

CREATE TABLE "Users" (
    "Id" uuid NOT NULL,
    "Username" character varying(256) NOT NULL,
    "NormalizedUsername" character varying(256) NOT NULL,
    "Email" character varying(320),
    "PasswordHash" character varying(512) NOT NULL,
    "SecurityStamp" character varying(128) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "IsSystemAdmin" boolean NOT NULL DEFAULT FALSE,
    CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
);

CREATE TABLE "Companies" (
    "Id" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_Companies" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Companies_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "OtpChallenges" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "ChallengeToken" character varying(128) NOT NULL,
    "CodeHash" character varying(256) NOT NULL,
    "Purpose" character varying(20) NOT NULL,
    "Channel" character varying(20) NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "ExpiresAtUtc" timestamptz NOT NULL,
    "ConsumedAtUtc" timestamptz,
    "ResendAvailableAtUtc" timestamptz NOT NULL,
    "AttemptCount" integer NOT NULL,
    "ResendCount" integer NOT NULL,
    CONSTRAINT "PK_OtpChallenges" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_OtpChallenges_Channel" CHECK ("Channel" IN ('Email', 'Sms', 'Authenticator')),
    CONSTRAINT "CK_OtpChallenges_Purpose" CHECK ("Purpose" IN ('Login')),
    CONSTRAINT "FK_OtpChallenges_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "UserDeviceBindings" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "DeviceId" character varying(200) NOT NULL,
    "DeviceType" character varying(100),
    "DeviceName" character varying(200),
    "RegisteredAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "LastAuthenticatedAtUtc" timestamptz NOT NULL,
    "Status" character varying(20) NOT NULL,
    "ResetAtUtc" timestamptz,
    "ResetByUserId" uuid,
    CONSTRAINT "PK_UserDeviceBindings" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_UserDeviceBindings_Status" CHECK ("Status" IN ('Active', 'Reset')),
    CONSTRAINT "FK_UserDeviceBindings_Users_ResetByUserId" FOREIGN KEY ("ResetByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserDeviceBindings_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "UserTwoFactorSettings" (
    "UserId" uuid NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "Channel" character varying(20) NOT NULL,
    "SharedSecret" character varying(256),
    CONSTRAINT "PK_UserTwoFactorSettings" PRIMARY KEY ("UserId"),
    CONSTRAINT "CK_UserTwoFactorSettings_Channel" CHECK ("Channel" IN ('Email', 'Sms', 'Authenticator')),
    CONSTRAINT "FK_UserTwoFactorSettings_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "CompanyConnections" (
    "Id" uuid NOT NULL,
    "SourceCompanyId" uuid NOT NULL,
    "TargetCompanyId" uuid NOT NULL,
    "ConnectionType" character varying(20) NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "PermissionLevel" character varying(20) NOT NULL,
    "PolicyRevision" bigint NOT NULL,
    "EffectiveFromUtc" timestamptz NOT NULL DEFAULT (now()),
    "UpdatedAtUtc" timestamptz,
    CONSTRAINT "PK_CompanyConnections" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_CompanyConnections_ConnectionType" CHECK ("ConnectionType" IN ('ParentToParent', 'ParentToChild', 'ChildToParent', 'ChildToChild')),
    CONSTRAINT "CK_CompanyConnections_NoSelf" CHECK ("SourceCompanyId" <> "TargetCompanyId"),
    CONSTRAINT "CK_CompanyConnections_PermissionLevel" CHECK ("PermissionLevel" IN ('Read', 'Write', 'Full')),
    CONSTRAINT "FK_CompanyConnections_Companies_SourceCompanyId" FOREIGN KEY ("SourceCompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_CompanyConnections_Companies_TargetCompanyId" FOREIGN KEY ("TargetCompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "Locations" (
    "Id" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "Region" character varying(200),
    "Name" character varying(200) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "UpdatedAtUtc" timestamptz,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_Locations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Locations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "UserCompanyMemberships" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "RoleId" uuid,
    "IsPrimary" boolean NOT NULL,
    CONSTRAINT "PK_UserCompanyMemberships" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_UserCompanyMemberships_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserCompanyMemberships_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserCompanyMemberships_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "CompanyConnectionFilters" (
    "Id" uuid NOT NULL,
    "CompanyConnectionId" uuid NOT NULL,
    "FilterType" character varying(20) NOT NULL,
    "FilterValue" character varying(400) NOT NULL,
    CONSTRAINT "PK_CompanyConnectionFilters" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_CompanyConnectionFilters_FilterType" CHECK ("FilterType" IN ('Region', 'Location', 'Warehouse', 'Category')),
    CONSTRAINT "FK_CompanyConnectionFilters_CompanyConnections_CompanyConnecti~" FOREIGN KEY ("CompanyConnectionId") REFERENCES "CompanyConnections" ("Id") ON DELETE CASCADE
);

CREATE TABLE "UserSessions" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "ActiveCompanyId" uuid NOT NULL,
    "ActiveLocationId" uuid,
    "IsTwoFactorComplete" boolean NOT NULL,
    "SecurityStamp" character varying(128) NOT NULL,
    "DeviceId" character varying(200),
    "RefreshTokenHash" character varying(256),
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "ExpiresAtUtc" timestamptz NOT NULL,
    "RevokedAtUtc" timestamptz,
    "LastSeenAtUtc" timestamptz NOT NULL,
    CONSTRAINT "PK_UserSessions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_UserSessions_Companies_ActiveCompanyId" FOREIGN KEY ("ActiveCompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserSessions_Locations_ActiveLocationId" FOREIGN KEY ("ActiveLocationId") REFERENCES "Locations" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_UserSessions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Warehouses" (
    "Id" uuid NOT NULL,
    "LocationId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "UpdatedAtUtc" timestamptz,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_Warehouses" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Warehouses_Locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES "Locations" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "Racks" (
    "Id" uuid NOT NULL,
    "WarehouseId" uuid NOT NULL,
    "LocationId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "UpdatedAtUtc" timestamptz,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_Racks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Racks_Warehouses_WarehouseId" FOREIGN KEY ("WarehouseId") REFERENCES "Warehouses" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "Bins" (
    "Id" uuid NOT NULL,
    "RackId" uuid NOT NULL,
    "WarehouseId" uuid NOT NULL,
    "LocationId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "Name" character varying(200) NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
    "UpdatedAtUtc" timestamptz,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_Bins" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Bins_Racks_RackId" FOREIGN KEY ("RackId") REFERENCES "Racks" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "CompanyConnectionScopes" (
    "Id" uuid NOT NULL,
    "CompanyConnectionId" uuid NOT NULL,
    "Level" character varying(20) NOT NULL,
    "ScopeCompanyId" uuid,
    "ScopeLocationId" uuid,
    "ScopeWarehouseId" uuid,
    "ScopeRackId" uuid,
    "ScopeBinId" uuid,
    "PermissionLevelOverride" character varying(20),
    CONSTRAINT "PK_CompanyConnectionScopes" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_CompanyConnectionScopes_Level" CHECK ("Level" IN ('Company', 'Location', 'Warehouse', 'Rack', 'Bin')),
    CONSTRAINT "CK_ConnScope_LevelMatches" CHECK (("Level" = 'Company'   AND "ScopeCompanyId"   IS NOT NULL) OR("Level" = 'Location'  AND "ScopeLocationId"  IS NOT NULL) OR("Level" = 'Warehouse' AND "ScopeWarehouseId" IS NOT NULL) OR("Level" = 'Rack'      AND "ScopeRackId"      IS NOT NULL) OR("Level" = 'Bin'       AND "ScopeBinId"       IS NOT NULL)),
    CONSTRAINT "CK_ConnScope_OneTarget" CHECK ((CASE WHEN "ScopeCompanyId"   IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "ScopeLocationId"  IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "ScopeWarehouseId" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "ScopeRackId"      IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN "ScopeBinId"       IS NOT NULL THEN 1 ELSE 0 END) = 1),
    CONSTRAINT "FK_CompanyConnectionScopes_Bins_ScopeBinId" FOREIGN KEY ("ScopeBinId") REFERENCES "Bins" ("Id"),
    CONSTRAINT "FK_CompanyConnectionScopes_Companies_ScopeCompanyId" FOREIGN KEY ("ScopeCompanyId") REFERENCES "Companies" ("Id"),
    CONSTRAINT "FK_CompanyConnectionScopes_CompanyConnections_CompanyConnectio~" FOREIGN KEY ("CompanyConnectionId") REFERENCES "CompanyConnections" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_CompanyConnectionScopes_Locations_ScopeLocationId" FOREIGN KEY ("ScopeLocationId") REFERENCES "Locations" ("Id"),
    CONSTRAINT "FK_CompanyConnectionScopes_Racks_ScopeRackId" FOREIGN KEY ("ScopeRackId") REFERENCES "Racks" ("Id"),
    CONSTRAINT "FK_CompanyConnectionScopes_Warehouses_ScopeWarehouseId" FOREIGN KEY ("ScopeWarehouseId") REFERENCES "Warehouses" ("Id")
);

CREATE INDEX "IX_Bins_CompanyId" ON "Bins" ("CompanyId");

CREATE INDEX "IX_Bins_RackId" ON "Bins" ("RackId");

CREATE INDEX "IX_Bins_TenantId" ON "Bins" ("TenantId");

CREATE INDEX "IX_Bins_WarehouseId" ON "Bins" ("WarehouseId");

CREATE INDEX "IX_Companies_TenantId" ON "Companies" ("TenantId");

CREATE INDEX "IX_CompanyConnectionFilters_CompanyConnectionId" ON "CompanyConnectionFilters" ("CompanyConnectionId");

CREATE UNIQUE INDEX "IX_CompanyConnections_SourceCompanyId_TargetCompanyId" ON "CompanyConnections" ("SourceCompanyId", "TargetCompanyId") INCLUDE ("IsEnabled", "PermissionLevel", "ConnectionType", "PolicyRevision");

CREATE INDEX "IX_CompanyConnections_TargetCompanyId" ON "CompanyConnections" ("TargetCompanyId");

CREATE INDEX "IX_CompanyConnectionScopes_CompanyConnectionId" ON "CompanyConnectionScopes" ("CompanyConnectionId") INCLUDE ("Level", "ScopeCompanyId", "ScopeLocationId", "ScopeWarehouseId", "ScopeRackId", "ScopeBinId", "PermissionLevelOverride");

CREATE INDEX "IX_CompanyConnectionScopes_ScopeBinId" ON "CompanyConnectionScopes" ("ScopeBinId");

CREATE INDEX "IX_CompanyConnectionScopes_ScopeCompanyId" ON "CompanyConnectionScopes" ("ScopeCompanyId");

CREATE INDEX "IX_CompanyConnectionScopes_ScopeLocationId" ON "CompanyConnectionScopes" ("ScopeLocationId");

CREATE INDEX "IX_CompanyConnectionScopes_ScopeRackId" ON "CompanyConnectionScopes" ("ScopeRackId");

CREATE INDEX "IX_CompanyConnectionScopes_ScopeWarehouseId" ON "CompanyConnectionScopes" ("ScopeWarehouseId");

CREATE INDEX "IX_Locations_CompanyId" ON "Locations" ("CompanyId");

CREATE INDEX "IX_Locations_CompanyId_Region" ON "Locations" ("CompanyId", "Region");

CREATE INDEX "IX_Locations_TenantId" ON "Locations" ("TenantId");

CREATE UNIQUE INDEX "IX_OtpChallenges_ChallengeToken" ON "OtpChallenges" ("ChallengeToken");

CREATE INDEX "IX_OtpChallenges_UserId_Purpose" ON "OtpChallenges" ("UserId", "Purpose") WHERE "ConsumedAtUtc" IS NULL;

CREATE INDEX "IX_Racks_CompanyId" ON "Racks" ("CompanyId");

CREATE INDEX "IX_Racks_TenantId" ON "Racks" ("TenantId");

CREATE INDEX "IX_Racks_WarehouseId" ON "Racks" ("WarehouseId");

CREATE UNIQUE INDEX "IX_Roles_Name" ON "Roles" ("Name");

CREATE INDEX "IX_Tenants_Kind" ON "Tenants" ("Kind");

CREATE INDEX "IX_UserCompanyMemberships_CompanyId" ON "UserCompanyMemberships" ("CompanyId");

CREATE INDEX "IX_UserCompanyMemberships_RoleId" ON "UserCompanyMemberships" ("RoleId");

CREATE UNIQUE INDEX "IX_UserCompanyMemberships_UserId_CompanyId" ON "UserCompanyMemberships" ("UserId", "CompanyId");

CREATE INDEX "IX_UserDeviceBindings_ResetByUserId" ON "UserDeviceBindings" ("ResetByUserId");

CREATE UNIQUE INDEX "IX_UserDeviceBindings_UserId" ON "UserDeviceBindings" ("UserId");

CREATE UNIQUE INDEX "IX_Users_NormalizedUsername" ON "Users" ("NormalizedUsername");

CREATE INDEX "IX_UserSessions_ActiveCompanyId" ON "UserSessions" ("ActiveCompanyId");

CREATE INDEX "IX_UserSessions_ActiveLocationId" ON "UserSessions" ("ActiveLocationId");

CREATE INDEX "IX_UserSessions_RefreshTokenHash" ON "UserSessions" ("RefreshTokenHash");

CREATE INDEX "IX_UserSessions_UserId" ON "UserSessions" ("UserId") WHERE "RevokedAtUtc" IS NULL;

CREATE INDEX "IX_Warehouses_CompanyId" ON "Warehouses" ("CompanyId");

CREATE INDEX "IX_Warehouses_LocationId" ON "Warehouses" ("LocationId");

CREATE INDEX "IX_Warehouses_TenantId" ON "Warehouses" ("TenantId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260914073546_InitialCreate', '10.0.4');

COMMIT;

