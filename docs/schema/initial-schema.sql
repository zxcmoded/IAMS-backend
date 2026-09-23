CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE TABLE "Roles" (
        "Id" uuid NOT NULL,
        "Name" character varying(128) NOT NULL,
        "Description" character varying(400),
        CONSTRAINT "PK_Roles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE TABLE "Tenants" (
        "Id" uuid NOT NULL,
        "Name" character varying(200) NOT NULL,
        "Kind" character varying(20) NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_Tenants_Kind" CHECK ("Kind" IN ('Parent', 'Child'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE TABLE "Companies" (
        "Id" uuid NOT NULL,
        "Name" character varying(200) NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        "TenantId" uuid NOT NULL,
        CONSTRAINT "PK_Companies" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Companies_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE TABLE "UserTwoFactorSettings" (
        "UserId" uuid NOT NULL,
        "IsEnabled" boolean NOT NULL,
        "Channel" character varying(20) NOT NULL,
        "SharedSecret" character varying(256),
        CONSTRAINT "PK_UserTwoFactorSettings" PRIMARY KEY ("UserId"),
        CONSTRAINT "CK_UserTwoFactorSettings_Channel" CHECK ("Channel" IN ('Email', 'Sms', 'Authenticator')),
        CONSTRAINT "FK_UserTwoFactorSettings_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE TABLE "CompanyConnectionFilters" (
        "Id" uuid NOT NULL,
        "CompanyConnectionId" uuid NOT NULL,
        "FilterType" character varying(20) NOT NULL,
        "FilterValue" character varying(400) NOT NULL,
        CONSTRAINT "PK_CompanyConnectionFilters" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_CompanyConnectionFilters_FilterType" CHECK ("FilterType" IN ('Region', 'Location', 'Warehouse', 'Category')),
        CONSTRAINT "FK_CompanyConnectionFilters_CompanyConnections_CompanyConnecti~" FOREIGN KEY ("CompanyConnectionId") REFERENCES "CompanyConnections" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Bins_CompanyId" ON "Bins" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Bins_RackId" ON "Bins" ("RackId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Bins_TenantId" ON "Bins" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Bins_WarehouseId" ON "Bins" ("WarehouseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Companies_TenantId" ON "Companies" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionFilters_CompanyConnectionId" ON "CompanyConnectionFilters" ("CompanyConnectionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_CompanyConnections_SourceCompanyId_TargetCompanyId" ON "CompanyConnections" ("SourceCompanyId", "TargetCompanyId") INCLUDE ("IsEnabled", "PermissionLevel", "ConnectionType", "PolicyRevision");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnections_TargetCompanyId" ON "CompanyConnections" ("TargetCompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionScopes_CompanyConnectionId" ON "CompanyConnectionScopes" ("CompanyConnectionId") INCLUDE ("Level", "ScopeCompanyId", "ScopeLocationId", "ScopeWarehouseId", "ScopeRackId", "ScopeBinId", "PermissionLevelOverride");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionScopes_ScopeBinId" ON "CompanyConnectionScopes" ("ScopeBinId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionScopes_ScopeCompanyId" ON "CompanyConnectionScopes" ("ScopeCompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionScopes_ScopeLocationId" ON "CompanyConnectionScopes" ("ScopeLocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionScopes_ScopeRackId" ON "CompanyConnectionScopes" ("ScopeRackId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_CompanyConnectionScopes_ScopeWarehouseId" ON "CompanyConnectionScopes" ("ScopeWarehouseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Locations_CompanyId" ON "Locations" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Locations_CompanyId_Region" ON "Locations" ("CompanyId", "Region");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Locations_TenantId" ON "Locations" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_OtpChallenges_ChallengeToken" ON "OtpChallenges" ("ChallengeToken");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_OtpChallenges_UserId_Purpose" ON "OtpChallenges" ("UserId", "Purpose") WHERE "ConsumedAtUtc" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Racks_CompanyId" ON "Racks" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Racks_TenantId" ON "Racks" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Racks_WarehouseId" ON "Racks" ("WarehouseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_Roles_Name" ON "Roles" ("Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Tenants_Kind" ON "Tenants" ("Kind");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserCompanyMemberships_CompanyId" ON "UserCompanyMemberships" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserCompanyMemberships_RoleId" ON "UserCompanyMemberships" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_UserCompanyMemberships_UserId_CompanyId" ON "UserCompanyMemberships" ("UserId", "CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserDeviceBindings_ResetByUserId" ON "UserDeviceBindings" ("ResetByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_UserDeviceBindings_UserId" ON "UserDeviceBindings" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_Users_NormalizedUsername" ON "Users" ("NormalizedUsername");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserSessions_ActiveCompanyId" ON "UserSessions" ("ActiveCompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserSessions_ActiveLocationId" ON "UserSessions" ("ActiveLocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserSessions_RefreshTokenHash" ON "UserSessions" ("RefreshTokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_UserSessions_UserId" ON "UserSessions" ("UserId") WHERE "RevokedAtUtc" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Warehouses_CompanyId" ON "Warehouses" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Warehouses_LocationId" ON "Warehouses" ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    CREATE INDEX "IX_Warehouses_TenantId" ON "Warehouses" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914073546_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260914073546_InitialCreate', '10.0.4');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    DROP TABLE "OtpChallenges";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    DROP TABLE "UserDeviceBindings";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    DROP TABLE "UserTwoFactorSettings";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    DROP INDEX "IX_Users_NormalizedUsername";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "UserSessions" DROP COLUMN "IsTwoFactorComplete";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" DROP COLUMN "NormalizedUsername";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" DROP COLUMN "PasswordHash";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD "ActivatedAtUtc" timestamptz;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD "ActivatedDeviceId" character varying(200);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD "ActivationKeyHash" character varying(128);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD "ActivationResetAtUtc" timestamptz;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD "ActivationResetByUserId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD "ActivationStatus" character varying(20);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    UPDATE "Users" SET "ActivationKeyHash" = 'legacy:' || "Id"::text, "ActivationStatus" = 'NotActivated' WHERE "ActivationKeyHash" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ALTER COLUMN "ActivationKeyHash" SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ALTER COLUMN "ActivationStatus" SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    CREATE UNIQUE INDEX "IX_Users_ActivationKeyHash" ON "Users" ("ActivationKeyHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    CREATE INDEX "IX_Users_ActivationResetByUserId" ON "Users" ("ActivationResetByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD CONSTRAINT "CK_Users_ActivationStatus" CHECK ("ActivationStatus" IN ('NotActivated', 'Activated'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    ALTER TABLE "Users" ADD CONSTRAINT "FK_Users_Users_ActivationResetByUserId" FOREIGN KEY ("ActivationResetByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260914104810_ActivationKeyAuthentication') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260914104810_ActivationKeyAuthentication', '10.0.4');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    ALTER TABLE "Companies" ADD "UpdatedAtUtc" timestamptz;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    ALTER TABLE "Warehouses" ADD "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    ALTER TABLE "Racks" ADD "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    ALTER TABLE "Locations" ADD "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    ALTER TABLE "Companies" ADD "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    ALTER TABLE "Bins" ADD "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    CREATE INDEX "IX_Warehouses_Sync" ON "Warehouses" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    CREATE INDEX "IX_Racks_Sync" ON "Racks" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    CREATE INDEX "IX_Locations_Sync" ON "Locations" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    CREATE INDEX "IX_Companies_Sync" ON "Companies" ("SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    CREATE INDEX "IX_Bins_Sync" ON "Bins" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260915060055_MasterDataSyncCursors') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260915060055_MasterDataSyncCursors', '10.0.4');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE TABLE "InventoryItems" (
        "Id" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "Sku" character varying(100) NOT NULL,
        "Barcode" character varying(100),
        "Name" character varying(200) NOT NULL,
        "Description" character varying(1000),
        "UnitOfMeasure" character varying(20),
        "Category" character varying(200),
        "IsActive" boolean NOT NULL,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        "UpdatedAtUtc" timestamptz,
        "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL,
        CONSTRAINT "PK_InventoryItems" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_InventoryItems_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE TABLE "InventorySettings" (
        "Id" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "VarianceThreshold" numeric(18,4) NOT NULL,
        "VarianceThresholdType" character varying(20) NOT NULL,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        "UpdatedAtUtc" timestamptz,
        CONSTRAINT "PK_InventorySettings" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_InventorySettings_VarianceThresholdType" CHECK ("VarianceThresholdType" IN ('AbsoluteQuantity', 'Percentage')),
        CONSTRAINT "FK_InventorySettings_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE TABLE "ScanEvents" (
        "Id" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "ScannedByUserId" uuid NOT NULL,
        "RawCode" character varying(400) NOT NULL,
        "ResolvedType" character varying(20) NOT NULL,
        "ResolvedEntityId" uuid,
        "DeviceId" character varying(200),
        "ScannedAtUtc" timestamptz NOT NULL,
        "IdempotencyKey" character varying(200),
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_ScanEvents" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_ScanEvents_ResolvedType" CHECK ("ResolvedType" IN ('Sku', 'Location', 'Asset', 'NoMatch', 'Blocked')),
        CONSTRAINT "FK_ScanEvents_Users_ScannedByUserId" FOREIGN KEY ("ScannedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE TABLE "InventoryTransactions" (
        "Id" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "InventoryItemId" uuid NOT NULL,
        "TransactionType" character varying(20) NOT NULL,
        "SourceBinId" uuid,
        "DestinationBinId" uuid,
        "Quantity" numeric(18,4) NOT NULL,
        "AdjustmentReason" character varying(400),
        "Status" character varying(20) NOT NULL,
        "RejectionReason" character varying(400),
        "IdempotencyKey" character varying(200) NOT NULL,
        "BaseSourceStockVersion" bigint,
        "BaseDestinationStockVersion" bigint,
        "ReversalOfTransactionId" uuid,
        "DeviceId" character varying(200),
        "CreatedByUserId" uuid NOT NULL,
        "ClientCreatedAtUtc" timestamptz,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        "UpdatedAtUtc" timestamptz,
        "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL,
        CONSTRAINT "PK_InventoryTransactions" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_InventoryTransactions_Status" CHECK ("Status" IN ('Applied', 'Rejected')),
        CONSTRAINT "CK_InventoryTransactions_TransactionType" CHECK ("TransactionType" IN ('Receive', 'Transfer', 'Adjustment')),
        CONSTRAINT "CK_InvTxn_AdjustmentReason" CHECK ("TransactionType" <> 'Adjustment' OR "AdjustmentReason" IS NOT NULL),
        CONSTRAINT "CK_InvTxn_TypeShape" CHECK (("TransactionType" = 'Receive'    AND "SourceBinId" IS NULL     AND "DestinationBinId" IS NOT NULL AND "Quantity" > 0) OR ("TransactionType" = 'Transfer'   AND "SourceBinId" IS NOT NULL AND "DestinationBinId" IS NOT NULL AND "SourceBinId" <> "DestinationBinId" AND "Quantity" > 0) OR ("TransactionType" = 'Adjustment' AND "SourceBinId" IS NOT NULL AND "DestinationBinId" IS NULL     AND "Quantity" <> 0)),
        CONSTRAINT "FK_InventoryTransactions_Bins_DestinationBinId" FOREIGN KEY ("DestinationBinId") REFERENCES "Bins" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_InventoryTransactions_Bins_SourceBinId" FOREIGN KEY ("SourceBinId") REFERENCES "Bins" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_InventoryTransactions_InventoryItems_InventoryItemId" FOREIGN KEY ("InventoryItemId") REFERENCES "InventoryItems" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_InventoryTransactions_InventoryTransactions_ReversalOfTrans~" FOREIGN KEY ("ReversalOfTransactionId") REFERENCES "InventoryTransactions" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_InventoryTransactions_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE TABLE "StockLevels" (
        "Id" uuid NOT NULL,
        "InventoryItemId" uuid NOT NULL,
        "BinId" uuid NOT NULL,
        "RackId" uuid NOT NULL,
        "WarehouseId" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "QuantityOnHand" numeric(18,4) NOT NULL,
        "Version" bigint NOT NULL,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        "UpdatedAtUtc" timestamptz,
        "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL,
        CONSTRAINT "PK_StockLevels" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_StockLevels_NonNegative" CHECK ("QuantityOnHand" >= 0),
        CONSTRAINT "FK_StockLevels_Bins_BinId" FOREIGN KEY ("BinId") REFERENCES "Bins" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_StockLevels_InventoryItems_InventoryItemId" FOREIGN KEY ("InventoryItemId") REFERENCES "InventoryItems" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE TABLE "StockCounts" (
        "Id" uuid NOT NULL,
        "TenantId" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "InventoryItemId" uuid NOT NULL,
        "BinId" uuid NOT NULL,
        "RackId" uuid NOT NULL,
        "WarehouseId" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "CountedQuantity" numeric(18,4) NOT NULL,
        "SystemQuantity" numeric(18,4) NOT NULL,
        "Variance" numeric(18,4) GENERATED ALWAYS AS ("CountedQuantity" - "SystemQuantity") STORED NOT NULL,
        "VarianceThreshold" numeric(18,4) NOT NULL,
        "VarianceThresholdType" character varying(20) NOT NULL,
        "Status" character varying(20) NOT NULL,
        "CountedByUserId" uuid NOT NULL,
        "ApprovedByUserId" uuid,
        "ApprovedAtUtc" timestamptz,
        "RejectionReason" character varying(400),
        "AdjustmentTransactionId" uuid,
        "IdempotencyKey" character varying(200) NOT NULL,
        "BaseStockVersion" bigint,
        "DeviceId" character varying(200),
        "ClientCreatedAtUtc" timestamptz,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        "UpdatedAtUtc" timestamptz,
        "SyncCursorUtc" timestamptz GENERATED ALWAYS AS (COALESCE("UpdatedAtUtc", "CreatedAtUtc")) STORED NOT NULL,
        CONSTRAINT "PK_StockCounts" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_StockCounts_CountedNonNegative" CHECK ("CountedQuantity" >= 0),
        CONSTRAINT "CK_StockCounts_Status" CHECK ("Status" IN ('Completed', 'PendingApproval', 'Approved', 'Rejected')),
        CONSTRAINT "CK_StockCounts_VarianceThresholdType" CHECK ("VarianceThresholdType" IN ('AbsoluteQuantity', 'Percentage')),
        CONSTRAINT "FK_StockCounts_Bins_BinId" FOREIGN KEY ("BinId") REFERENCES "Bins" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_StockCounts_InventoryItems_InventoryItemId" FOREIGN KEY ("InventoryItemId") REFERENCES "InventoryItems" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_StockCounts_InventoryTransactions_AdjustmentTransactionId" FOREIGN KEY ("AdjustmentTransactionId") REFERENCES "InventoryTransactions" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_StockCounts_Users_ApprovedByUserId" FOREIGN KEY ("ApprovedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_StockCounts_Users_CountedByUserId" FOREIGN KEY ("CountedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryItems_CompanyId_Barcode" ON "InventoryItems" ("CompanyId", "Barcode") WHERE "Barcode" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE UNIQUE INDEX "IX_InventoryItems_CompanyId_Sku" ON "InventoryItems" ("CompanyId", "Sku");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryItems_Sync" ON "InventoryItems" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryItems_TenantId" ON "InventoryItems" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE UNIQUE INDEX "IX_InventorySettings_CompanyId" ON "InventorySettings" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_CompanyId_CreatedAtUtc" ON "InventoryTransactions" ("CompanyId", "CreatedAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_CreatedByUserId" ON "InventoryTransactions" ("CreatedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_DestinationBinId" ON "InventoryTransactions" ("DestinationBinId") WHERE "DestinationBinId" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE UNIQUE INDEX "IX_InventoryTransactions_IdempotencyKey" ON "InventoryTransactions" ("IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_InventoryItemId" ON "InventoryTransactions" ("InventoryItemId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_ReversalOfTransactionId" ON "InventoryTransactions" ("ReversalOfTransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_SourceBinId" ON "InventoryTransactions" ("SourceBinId") WHERE "SourceBinId" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_Sync" ON "InventoryTransactions" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_InventoryTransactions_TenantId" ON "InventoryTransactions" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_ScanEvents_CompanyId_RawCode" ON "ScanEvents" ("CompanyId", "RawCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_ScanEvents_CompanyId_ScannedAtUtc" ON "ScanEvents" ("CompanyId", "ScannedAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE UNIQUE INDEX "IX_ScanEvents_IdempotencyKey" ON "ScanEvents" ("IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_ScanEvents_ScannedByUserId_ScannedAtUtc" ON "ScanEvents" ("ScannedByUserId", "ScannedAtUtc");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_ScanEvents_TenantId" ON "ScanEvents" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_AdjustmentTransactionId" ON "StockCounts" ("AdjustmentTransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_ApprovedByUserId" ON "StockCounts" ("ApprovedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_BinId" ON "StockCounts" ("BinId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_CompanyId_Status" ON "StockCounts" ("CompanyId", "Status") WHERE "Status" = 'PendingApproval';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_CountedByUserId" ON "StockCounts" ("CountedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE UNIQUE INDEX "IX_StockCounts_IdempotencyKey" ON "StockCounts" ("IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_InventoryItemId" ON "StockCounts" ("InventoryItemId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_Sync" ON "StockCounts" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockCounts_TenantId" ON "StockCounts" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockLevels_BinId" ON "StockLevels" ("BinId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockLevels_CompanyId_InventoryItemId" ON "StockLevels" ("CompanyId", "InventoryItemId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE UNIQUE INDEX "IX_StockLevels_InventoryItemId_BinId" ON "StockLevels" ("InventoryItemId", "BinId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockLevels_Sync" ON "StockLevels" ("CompanyId", "SyncCursorUtc", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    CREATE INDEX "IX_StockLevels_TenantId" ON "StockLevels" ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260916074619_AddInventoryAndScanning') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260916074619_AddInventoryAndScanning', '10.0.4');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260917051030_DropRefreshTokenFromUserSession') THEN
    DROP INDEX "IX_UserSessions_RefreshTokenHash";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260917051030_DropRefreshTokenFromUserSession') THEN
    ALTER TABLE "UserSessions" DROP COLUMN "RefreshTokenHash";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260917051030_DropRefreshTokenFromUserSession') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260917051030_DropRefreshTokenFromUserSession', '10.0.4');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Companies" DROP CONSTRAINT "FK_Companies_Tenants_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "UserSessions" DROP CONSTRAINT "FK_UserSessions_Companies_ActiveCompanyId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "UserSessions" DROP CONSTRAINT "FK_UserSessions_Locations_ActiveLocationId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP TABLE "CompanyConnectionFilters";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP TABLE "CompanyConnectionScopes";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP TABLE "Tenants";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP TABLE "UserCompanyMemberships";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP TABLE "CompanyConnections";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP TABLE "Roles";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_Warehouses_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_UserSessions_ActiveCompanyId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_UserSessions_ActiveLocationId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_StockLevels_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_StockCounts_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_ScanEvents_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_Racks_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_Locations_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_InventoryTransactions_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_InventoryItems_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_Companies_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    DROP INDEX "IX_Bins_TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Warehouses" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "UserSessions" DROP COLUMN "ActiveCompanyId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "UserSessions" DROP COLUMN "ActiveLocationId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Users" DROP COLUMN "IsSystemAdmin";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "StockLevels" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "StockCounts" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "ScanEvents" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Racks" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Locations" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "InventoryTransactions" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "InventorySettings" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "InventoryItems" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Companies" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Bins" DROP COLUMN "TenantId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    INSERT INTO "Companies" ("Id", "Name", "IsActive")
    VALUES ('11111111-1111-1111-1111-111111111111', 'Unassigned', TRUE)
    ON CONFLICT ("Id") DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Users" ADD "CompanyId" uuid NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Users" ADD "Role" integer NOT NULL DEFAULT 100;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    CREATE TABLE "UserLocationAssignments" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "CreatedAtUtc" timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_UserLocationAssignments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_UserLocationAssignments_Locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES "Locations" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_UserLocationAssignments_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    CREATE INDEX "IX_Users_CompanyId" ON "Users" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Users" ADD CONSTRAINT "CK_Users_Role" CHECK ("Role" IN (100, 200, 300, 700, 800));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    CREATE INDEX "IX_UserLocationAssignments_LocationId" ON "UserLocationAssignments" ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    CREATE UNIQUE INDEX "IX_UserLocationAssignments_UserId_LocationId" ON "UserLocationAssignments" ("UserId", "LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    ALTER TABLE "Users" ADD CONSTRAINT "FK_Users_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260922054045_RemoveTenantAndCompanyConnections') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260922054045_RemoveTenantAndCompanyConnections', '10.0.4');
    END IF;
END $EF$;
COMMIT;

