-- OpenIddict EF Core stores (bigint IDENTITY keys). Baseline generated from AuthDbContext via `generate-auth-ddl`
-- (OpenIddict.EntityFrameworkCore 7.x). Keep in sync with the EF model on package upgrades.
SET XACT_ABORT ON;

CREATE TABLE [auth].[OpenIddictApplications] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [ApplicationType] nvarchar(50) NULL,
    [ClientId] nvarchar(100) NULL,
    [ClientSecret] nvarchar(max) NULL,
    [ClientType] nvarchar(50) NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [ConsentType] nvarchar(50) NULL,
    [DisplayName] nvarchar(max) NULL,
    [DisplayNames] nvarchar(max) NULL,
    [JsonWebKeySet] nvarchar(max) NULL,
    [Permissions] nvarchar(max) NULL,
    [PostLogoutRedirectUris] nvarchar(max) NULL,
    [Properties] nvarchar(max) NULL,
    [RedirectUris] nvarchar(max) NULL,
    [Requirements] nvarchar(max) NULL,
    [Settings] nvarchar(max) NULL,
    CONSTRAINT [PK_OpenIddictApplications] PRIMARY KEY CLUSTERED ([Id])
);

CREATE TABLE [auth].[OpenIddictScopes] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [ConcurrencyToken] nvarchar(50) NULL,
    [Description] nvarchar(max) NULL,
    [Descriptions] nvarchar(max) NULL,
    [DisplayName] nvarchar(max) NULL,
    [DisplayNames] nvarchar(max) NULL,
    [Name] nvarchar(200) NULL,
    [Properties] nvarchar(max) NULL,
    [Resources] nvarchar(max) NULL,
    CONSTRAINT [PK_OpenIddictScopes] PRIMARY KEY CLUSTERED ([Id])
);

CREATE TABLE [auth].[OpenIddictAuthorizations] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [ApplicationId] bigint NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [CreationDate] datetime2 NULL,
    [Properties] nvarchar(max) NULL,
    [Scopes] nvarchar(max) NULL,
    [Status] nvarchar(50) NULL,
    [Subject] nvarchar(400) NULL,
    [Type] nvarchar(50) NULL,
    CONSTRAINT [PK_OpenIddictAuthorizations] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [auth].[OpenIddictApplications] ([Id])
);

CREATE TABLE [auth].[OpenIddictTokens] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [ApplicationId] bigint NULL,
    [AuthorizationId] bigint NULL,
    [ConcurrencyToken] nvarchar(50) NULL,
    [CreationDate] datetime2 NULL,
    [ExpirationDate] datetime2 NULL,
    [Payload] nvarchar(max) NULL,
    [Properties] nvarchar(max) NULL,
    [RedemptionDate] datetime2 NULL,
    [ReferenceId] nvarchar(100) NULL,
    [Status] nvarchar(50) NULL,
    [Subject] nvarchar(400) NULL,
    [Type] nvarchar(150) NULL,
    CONSTRAINT [PK_OpenIddictTokens] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_OpenIddictTokens_OpenIddictApplications_ApplicationId] FOREIGN KEY ([ApplicationId]) REFERENCES [auth].[OpenIddictApplications] ([Id]),
    CONSTRAINT [FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId] FOREIGN KEY ([AuthorizationId]) REFERENCES [auth].[OpenIddictAuthorizations] ([Id])
);

CREATE UNIQUE INDEX [IX_OpenIddictApplications_ClientId] ON [auth].[OpenIddictApplications] ([ClientId]) WHERE [ClientId] IS NOT NULL;
CREATE INDEX [IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type] ON [auth].[OpenIddictAuthorizations] ([ApplicationId], [Status], [Subject], [Type]);
CREATE UNIQUE INDEX [IX_OpenIddictScopes_Name] ON [auth].[OpenIddictScopes] ([Name]) WHERE [Name] IS NOT NULL;
CREATE INDEX [IX_OpenIddictTokens_ApplicationId_Status_Subject_Type] ON [auth].[OpenIddictTokens] ([ApplicationId], [Status], [Subject], [Type]);
CREATE INDEX [IX_OpenIddictTokens_AuthorizationId] ON [auth].[OpenIddictTokens] ([AuthorizationId]);
CREATE UNIQUE INDEX [IX_OpenIddictTokens_ReferenceId] ON [auth].[OpenIddictTokens] ([ReferenceId]) WHERE [ReferenceId] IS NOT NULL;
