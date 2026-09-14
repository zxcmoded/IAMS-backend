IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [Roles] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(128) NOT NULL,
    [Description] nvarchar(400) NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
);

CREATE TABLE [Tenants] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Kind] nvarchar(20) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Tenants] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_Tenants_Kind] CHECK ([Kind] IN ('Parent', 'Child'))
);

CREATE TABLE [Users] (
    [Id] uniqueidentifier NOT NULL,
    [Username] nvarchar(256) NOT NULL,
    [NormalizedUsername] nvarchar(256) NOT NULL,
    [Email] nvarchar(320) NULL,
    [PasswordHash] nvarchar(512) NOT NULL,
    [SecurityStamp] nvarchar(128) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
);

CREATE TABLE [Companies] (
    [Id] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Companies] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Companies_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [Tenants] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [OtpChallenges] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [ChallengeToken] nvarchar(256) NOT NULL,
    [CodeHash] nvarchar(256) NOT NULL,
    [Purpose] nvarchar(20) NOT NULL,
    [Channel] nvarchar(20) NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [ExpiresAtUtc] datetime2 NOT NULL,
    [ConsumedAtUtc] datetime2 NULL,
    [AttemptCount] int NOT NULL,
    CONSTRAINT [PK_OtpChallenges] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_OtpChallenges_Channel] CHECK ([Channel] IN ('Email', 'Sms', 'Authenticator')),
    CONSTRAINT [CK_OtpChallenges_Purpose] CHECK ([Purpose] IN ('Login')),
    CONSTRAINT [FK_OtpChallenges_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [UserTwoFactorSettings] (
    [UserId] uniqueidentifier NOT NULL,
    [IsEnabled] bit NOT NULL,
    [Channel] nvarchar(20) NOT NULL,
    [SharedSecret] nvarchar(256) NULL,
    CONSTRAINT [PK_UserTwoFactorSettings] PRIMARY KEY ([UserId]),
    CONSTRAINT [CK_UserTwoFactorSettings_Channel] CHECK ([Channel] IN ('Email', 'Sms', 'Authenticator')),
    CONSTRAINT [FK_UserTwoFactorSettings_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [CompanyConnections] (
    [Id] uniqueidentifier NOT NULL,
    [SourceCompanyId] uniqueidentifier NOT NULL,
    [TargetCompanyId] uniqueidentifier NOT NULL,
    [ConnectionType] nvarchar(20) NOT NULL,
    [IsEnabled] bit NOT NULL,
    [PermissionLevel] nvarchar(20) NOT NULL,
    [PolicyRevision] bigint NOT NULL,
    [EffectiveFromUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc] datetime2 NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_CompanyConnections] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CompanyConnections_ConnectionType] CHECK ([ConnectionType] IN ('ParentToParent', 'ParentToChild', 'ChildToParent', 'ChildToChild')),
    CONSTRAINT [CK_CompanyConnections_NoSelf] CHECK ([SourceCompanyId] <> [TargetCompanyId]),
    CONSTRAINT [CK_CompanyConnections_PermissionLevel] CHECK ([PermissionLevel] IN ('Read', 'Write', 'Full')),
    CONSTRAINT [FK_CompanyConnections_Companies_SourceCompanyId] FOREIGN KEY ([SourceCompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_CompanyConnections_Companies_TargetCompanyId] FOREIGN KEY ([TargetCompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [Locations] (
    [Id] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [Region] nvarchar(200) NULL,
    [Name] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc] datetime2 NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Locations] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Locations_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [UserCompanyMemberships] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [RoleId] uniqueidentifier NULL,
    [IsPrimary] bit NOT NULL,
    CONSTRAINT [PK_UserCompanyMemberships] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_UserCompanyMemberships_Companies_CompanyId] FOREIGN KEY ([CompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_UserCompanyMemberships_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_UserCompanyMemberships_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [CompanyConnectionFilters] (
    [Id] uniqueidentifier NOT NULL,
    [CompanyConnectionId] uniqueidentifier NOT NULL,
    [FilterType] nvarchar(20) NOT NULL,
    [FilterValue] nvarchar(400) NOT NULL,
    CONSTRAINT [PK_CompanyConnectionFilters] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CompanyConnectionFilters_FilterType] CHECK ([FilterType] IN ('Region', 'Location', 'Warehouse', 'Category')),
    CONSTRAINT [FK_CompanyConnectionFilters_CompanyConnections_CompanyConnectionId] FOREIGN KEY ([CompanyConnectionId]) REFERENCES [CompanyConnections] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [UserSessions] (
    [Id] uniqueidentifier NOT NULL,
    [UserId] uniqueidentifier NOT NULL,
    [ActiveCompanyId] uniqueidentifier NOT NULL,
    [ActiveLocationId] uniqueidentifier NULL,
    [IsTwoFactorComplete] bit NOT NULL,
    [DeviceId] nvarchar(200) NULL,
    [RefreshTokenHash] nvarchar(256) NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [ExpiresAtUtc] datetime2 NOT NULL,
    [RevokedAtUtc] datetime2 NULL,
    [LastSeenAtUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_UserSessions] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_UserSessions_Companies_ActiveCompanyId] FOREIGN KEY ([ActiveCompanyId]) REFERENCES [Companies] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_UserSessions_Locations_ActiveLocationId] FOREIGN KEY ([ActiveLocationId]) REFERENCES [Locations] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_UserSessions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [Warehouses] (
    [Id] uniqueidentifier NOT NULL,
    [LocationId] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc] datetime2 NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Warehouses] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Warehouses_Locations_LocationId] FOREIGN KEY ([LocationId]) REFERENCES [Locations] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [Racks] (
    [Id] uniqueidentifier NOT NULL,
    [WarehouseId] uniqueidentifier NOT NULL,
    [LocationId] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc] datetime2 NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Racks] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Racks_Warehouses_WarehouseId] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [Bins] (
    [Id] uniqueidentifier NOT NULL,
    [RackId] uniqueidentifier NOT NULL,
    [WarehouseId] uniqueidentifier NOT NULL,
    [LocationId] uniqueidentifier NOT NULL,
    [CompanyId] uniqueidentifier NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL DEFAULT (SYSUTCDATETIME()),
    [UpdatedAtUtc] datetime2 NULL,
    [TenantId] uniqueidentifier NOT NULL,
    CONSTRAINT [PK_Bins] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Bins_Racks_RackId] FOREIGN KEY ([RackId]) REFERENCES [Racks] ([Id]) ON DELETE NO ACTION
);

CREATE TABLE [CompanyConnectionScopes] (
    [Id] uniqueidentifier NOT NULL,
    [CompanyConnectionId] uniqueidentifier NOT NULL,
    [Level] nvarchar(20) NOT NULL,
    [ScopeCompanyId] uniqueidentifier NULL,
    [ScopeLocationId] uniqueidentifier NULL,
    [ScopeWarehouseId] uniqueidentifier NULL,
    [ScopeRackId] uniqueidentifier NULL,
    [ScopeBinId] uniqueidentifier NULL,
    [PermissionLevelOverride] nvarchar(20) NULL,
    CONSTRAINT [PK_CompanyConnectionScopes] PRIMARY KEY ([Id]),
    CONSTRAINT [CK_CompanyConnectionScopes_Level] CHECK ([Level] IN ('Company', 'Location', 'Warehouse', 'Rack', 'Bin')),
    CONSTRAINT [CK_ConnScope_LevelMatches] CHECK (([Level] = 'Company'   AND [ScopeCompanyId]   IS NOT NULL) OR([Level] = 'Location'  AND [ScopeLocationId]  IS NOT NULL) OR([Level] = 'Warehouse' AND [ScopeWarehouseId] IS NOT NULL) OR([Level] = 'Rack'      AND [ScopeRackId]      IS NOT NULL) OR([Level] = 'Bin'       AND [ScopeBinId]       IS NOT NULL)),
    CONSTRAINT [CK_ConnScope_OneTarget] CHECK ((CASE WHEN [ScopeCompanyId]   IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN [ScopeLocationId]  IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN [ScopeWarehouseId] IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN [ScopeRackId]      IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN [ScopeBinId]       IS NOT NULL THEN 1 ELSE 0 END) = 1),
    CONSTRAINT [FK_CompanyConnectionScopes_Bins_ScopeBinId] FOREIGN KEY ([ScopeBinId]) REFERENCES [Bins] ([Id]),
    CONSTRAINT [FK_CompanyConnectionScopes_Companies_ScopeCompanyId] FOREIGN KEY ([ScopeCompanyId]) REFERENCES [Companies] ([Id]),
    CONSTRAINT [FK_CompanyConnectionScopes_CompanyConnections_CompanyConnectionId] FOREIGN KEY ([CompanyConnectionId]) REFERENCES [CompanyConnections] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_CompanyConnectionScopes_Locations_ScopeLocationId] FOREIGN KEY ([ScopeLocationId]) REFERENCES [Locations] ([Id]),
    CONSTRAINT [FK_CompanyConnectionScopes_Racks_ScopeRackId] FOREIGN KEY ([ScopeRackId]) REFERENCES [Racks] ([Id]),
    CONSTRAINT [FK_CompanyConnectionScopes_Warehouses_ScopeWarehouseId] FOREIGN KEY ([ScopeWarehouseId]) REFERENCES [Warehouses] ([Id])
);

CREATE INDEX [IX_Bins_CompanyId] ON [Bins] ([CompanyId]);

CREATE INDEX [IX_Bins_RackId] ON [Bins] ([RackId]);

CREATE INDEX [IX_Bins_TenantId] ON [Bins] ([TenantId]);

CREATE INDEX [IX_Bins_WarehouseId] ON [Bins] ([WarehouseId]);

CREATE INDEX [IX_Companies_TenantId] ON [Companies] ([TenantId]);

CREATE INDEX [IX_CompanyConnectionFilters_CompanyConnectionId] ON [CompanyConnectionFilters] ([CompanyConnectionId]);

CREATE UNIQUE INDEX [IX_CompanyConnections_SourceCompanyId_TargetCompanyId] ON [CompanyConnections] ([SourceCompanyId], [TargetCompanyId]) INCLUDE ([IsEnabled], [PermissionLevel], [ConnectionType], [PolicyRevision]);

CREATE INDEX [IX_CompanyConnections_TargetCompanyId] ON [CompanyConnections] ([TargetCompanyId]);

CREATE INDEX [IX_CompanyConnectionScopes_CompanyConnectionId] ON [CompanyConnectionScopes] ([CompanyConnectionId]) INCLUDE ([Level], [ScopeCompanyId], [ScopeLocationId], [ScopeWarehouseId], [ScopeRackId], [ScopeBinId], [PermissionLevelOverride]);

CREATE INDEX [IX_CompanyConnectionScopes_ScopeBinId] ON [CompanyConnectionScopes] ([ScopeBinId]);

CREATE INDEX [IX_CompanyConnectionScopes_ScopeCompanyId] ON [CompanyConnectionScopes] ([ScopeCompanyId]);

CREATE INDEX [IX_CompanyConnectionScopes_ScopeLocationId] ON [CompanyConnectionScopes] ([ScopeLocationId]);

CREATE INDEX [IX_CompanyConnectionScopes_ScopeRackId] ON [CompanyConnectionScopes] ([ScopeRackId]);

CREATE INDEX [IX_CompanyConnectionScopes_ScopeWarehouseId] ON [CompanyConnectionScopes] ([ScopeWarehouseId]);

CREATE INDEX [IX_Locations_CompanyId] ON [Locations] ([CompanyId]);

CREATE INDEX [IX_Locations_CompanyId_Region] ON [Locations] ([CompanyId], [Region]);

CREATE INDEX [IX_Locations_TenantId] ON [Locations] ([TenantId]);

CREATE UNIQUE INDEX [IX_OtpChallenges_ChallengeToken] ON [OtpChallenges] ([ChallengeToken]);

CREATE INDEX [IX_OtpChallenges_UserId_Purpose] ON [OtpChallenges] ([UserId], [Purpose]) WHERE [ConsumedAtUtc] IS NULL;

CREATE INDEX [IX_Racks_CompanyId] ON [Racks] ([CompanyId]);

CREATE INDEX [IX_Racks_TenantId] ON [Racks] ([TenantId]);

CREATE INDEX [IX_Racks_WarehouseId] ON [Racks] ([WarehouseId]);

CREATE UNIQUE INDEX [IX_Roles_Name] ON [Roles] ([Name]);

CREATE INDEX [IX_Tenants_Kind] ON [Tenants] ([Kind]);

CREATE INDEX [IX_UserCompanyMemberships_CompanyId] ON [UserCompanyMemberships] ([CompanyId]);

CREATE INDEX [IX_UserCompanyMemberships_RoleId] ON [UserCompanyMemberships] ([RoleId]);

CREATE UNIQUE INDEX [IX_UserCompanyMemberships_UserId_CompanyId] ON [UserCompanyMemberships] ([UserId], [CompanyId]);

CREATE UNIQUE INDEX [IX_Users_NormalizedUsername] ON [Users] ([NormalizedUsername]);

CREATE INDEX [IX_UserSessions_ActiveCompanyId] ON [UserSessions] ([ActiveCompanyId]);

CREATE INDEX [IX_UserSessions_ActiveLocationId] ON [UserSessions] ([ActiveLocationId]);

CREATE INDEX [IX_UserSessions_RefreshTokenHash] ON [UserSessions] ([RefreshTokenHash]);

CREATE INDEX [IX_UserSessions_UserId] ON [UserSessions] ([UserId]) WHERE [RevokedAtUtc] IS NULL;

CREATE INDEX [IX_Warehouses_CompanyId] ON [Warehouses] ([CompanyId]);

CREATE INDEX [IX_Warehouses_LocationId] ON [Warehouses] ([LocationId]);

CREATE INDEX [IX_Warehouses_TenantId] ON [Warehouses] ([TenantId]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260909124455_InitialCreate', N'10.0.10');

COMMIT;
GO

