-- Permission catalogue, role-permission matrix and tenant-scoped role memberships.
-- Dapper entities: EduEco.Core.Authorization.{Permission, RolePermission, UserTenantMembership}.
SET XACT_ABORT ON;

-- Code-owned catalogue: explicit ids (no IDENTITY), maintained by 02_ReferenceData.
CREATE TABLE [auth].[Permissions] (
    [Id] bigint NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [GroupName] nvarchar(100) NOT NULL,
    [Description] nvarchar(400) NOT NULL,
    CONSTRAINT [PK_Permissions] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Permissions_Name] UNIQUE NONCLUSTERED ([Name])
);

CREATE TABLE [auth].[RolePermissions] (
    [RoleId] bigint NOT NULL,
    [PermissionId] bigint NOT NULL,
    CONSTRAINT [PK_RolePermissions] PRIMARY KEY CLUSTERED ([RoleId], [PermissionId]),
    CONSTRAINT [FK_RolePermissions_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [auth].[AspNetRoles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_RolePermissions_Permissions_PermissionId] FOREIGN KEY ([PermissionId]) REFERENCES [auth].[Permissions] ([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_RolePermissions_PermissionId] ON [auth].[RolePermissions] ([PermissionId]);

CREATE TABLE [auth].[UserTenantMemberships] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [TenantId] bigint NOT NULL,
    [UserId] bigint NOT NULL,
    [RoleId] bigint NOT NULL,
    [IsDefault] bit NOT NULL CONSTRAINT [DF_UserTenantMemberships_IsDefault] DEFAULT (0),
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [CreatedBy] nvarchar(256) NOT NULL,
    [UpdatedAtUtc] datetimeoffset(7) NULL,
    [UpdatedBy] nvarchar(256) NULL,
    CONSTRAINT [PK_UserTenantMemberships] PRIMARY KEY CLUSTERED ([Id]),
    -- Serves the permission lookup (UserId, TenantId) as a covering seek.
    CONSTRAINT [UQ_UserTenantMemberships_User_Tenant_Role] UNIQUE NONCLUSTERED ([UserId], [TenantId], [RoleId]),
    CONSTRAINT [FK_UserTenantMemberships_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([Id]),
    CONSTRAINT [FK_UserTenantMemberships_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserTenantMemberships_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [auth].[AspNetRoles] ([Id])
);

CREATE INDEX [IX_UserTenantMemberships_TenantId] ON [auth].[UserTenantMemberships] ([TenantId]);
CREATE INDEX [IX_UserTenantMemberships_RoleId] ON [auth].[UserTenantMemberships] ([RoleId]);
