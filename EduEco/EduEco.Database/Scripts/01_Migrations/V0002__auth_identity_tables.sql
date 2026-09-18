-- ASP.NET Core Identity tables (bigint IDENTITY keys). Baseline generated from AuthDbContext via `generate-auth-ddl`
-- (Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.x). Keep in sync with the EF model on package upgrades.
-- Clustered bigint IDENTITY PKs: narrow, ever-increasing (append-only inserts, no page splits).
-- Claims tables keep int IDENTITY: IdentityUserClaim/IdentityRoleClaim.Id is fixed to int by ASP.NET Core Identity.
SET XACT_ABORT ON;

-- Ids 1-999 reserved for code-owned system roles (02_ReferenceData inserts them with IDENTITY_INSERT).
CREATE TABLE [auth].[AspNetRoles] (
    [Id] bigint NOT NULL IDENTITY(1000, 1),
    [Description] nvarchar(400) NULL,
    [IsSystem] bit NOT NULL CONSTRAINT [DF_AspNetRoles_IsSystem] DEFAULT (0),
    [Name] nvarchar(256) NULL,
    [NormalizedName] nvarchar(256) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetRoles] PRIMARY KEY CLUSTERED ([Id])
);

CREATE TABLE [auth].[AspNetUsers] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [DisplayName] nvarchar(200) NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_AspNetUsers_IsActive] DEFAULT (1),
    [UserName] nvarchar(256) NULL,
    [NormalizedUserName] nvarchar(256) NULL,
    [Email] nvarchar(256) NULL,
    [NormalizedEmail] nvarchar(256) NULL,
    [EmailConfirmed] bit NOT NULL,
    [PasswordHash] nvarchar(max) NULL,
    [SecurityStamp] nvarchar(max) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    [PhoneNumber] nvarchar(max) NULL,
    [PhoneNumberConfirmed] bit NOT NULL,
    [TwoFactorEnabled] bit NOT NULL,
    [LockoutEnd] datetimeoffset NULL,
    [LockoutEnabled] bit NOT NULL,
    [AccessFailedCount] int NOT NULL,
    CONSTRAINT [PK_AspNetUsers] PRIMARY KEY CLUSTERED ([Id])
);

CREATE TABLE [auth].[AspNetRoleClaims] (
    [Id] int NOT NULL IDENTITY,
    [RoleId] bigint NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [auth].[AspNetRoles] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [auth].[AspNetUserClaims] (
    [Id] int NOT NULL IDENTITY,
    [UserId] bigint NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [auth].[AspNetUserLogins] (
    [LoginProvider] nvarchar(450) NOT NULL,
    [ProviderKey] nvarchar(450) NOT NULL,
    [ProviderDisplayName] nvarchar(max) NULL,
    [UserId] bigint NOT NULL,
    CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
    CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [auth].[AspNetUserRoles] (
    [UserId] bigint NOT NULL,
    [RoleId] bigint NOT NULL,
    CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [auth].[AspNetRoles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE TABLE [auth].[AspNetUserTokens] (
    [UserId] bigint NOT NULL,
    [LoginProvider] nvarchar(450) NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Value] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
    CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [auth].[AspNetRoleClaims] ([RoleId]);
CREATE UNIQUE INDEX [RoleNameIndex] ON [auth].[AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL;
CREATE INDEX [IX_AspNetUserClaims_UserId] ON [auth].[AspNetUserClaims] ([UserId]);
CREATE INDEX [IX_AspNetUserLogins_UserId] ON [auth].[AspNetUserLogins] ([UserId]);
CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [auth].[AspNetUserRoles] ([RoleId]);
CREATE INDEX [EmailIndex] ON [auth].[AspNetUsers] ([NormalizedEmail]);
CREATE UNIQUE INDEX [UserNameIndex] ON [auth].[AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL;
