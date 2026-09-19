-- Tenant invite codes: single-use, expiring, redeemed during self-registration to join an existing tenant.
-- Dapper entity: EduEco.Core.Authorization.TenantInvite. Code is stored hashed (SHA-256); the plaintext is shown once at issuance.
SET XACT_ABORT ON;

CREATE TABLE [auth].[TenantInvites] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [TenantId] bigint NOT NULL,
    [CodeHash] nvarchar(64) NOT NULL,
    [RoleId] bigint NOT NULL,
    [ExpiresAtUtc] datetimeoffset(7) NOT NULL,
    [MaxUses] int NOT NULL CONSTRAINT [DF_TenantInvites_MaxUses] DEFAULT (1),
    [UseCount] int NOT NULL CONSTRAINT [DF_TenantInvites_UseCount] DEFAULT (0),
    [RedeemedByUserId] bigint NULL,
    [RedeemedAtUtc] datetimeoffset(7) NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [CreatedBy] nvarchar(256) NOT NULL,
    [UpdatedAtUtc] datetimeoffset(7) NULL,
    [UpdatedBy] nvarchar(256) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_TenantInvites] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_TenantInvites_CodeHash] UNIQUE NONCLUSTERED ([CodeHash]),
    CONSTRAINT [FK_TenantInvites_Tenants_TenantId] FOREIGN KEY ([TenantId]) REFERENCES [dbo].[Tenants] ([Id]),
    CONSTRAINT [FK_TenantInvites_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [auth].[AspNetRoles] ([Id]),
    CONSTRAINT [FK_TenantInvites_AspNetUsers_RedeemedByUserId] FOREIGN KEY ([RedeemedByUserId]) REFERENCES [auth].[AspNetUsers] ([Id])
);

CREATE INDEX [IX_TenantInvites_TenantId] ON [auth].[TenantInvites] ([TenantId]);
