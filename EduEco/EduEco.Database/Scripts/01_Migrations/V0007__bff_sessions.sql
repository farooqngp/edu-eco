-- Server-side BFF sessions (EduEco.Bff). The browser cookie holds only a random session key; tickets and OAuth tokens
-- stay here, encrypted with ASP.NET Core Data Protection. The key itself is stored as a SHA-256 hash.
SET XACT_ABORT ON;

IF SCHEMA_ID(N'bff') IS NULL
    EXEC (N'CREATE SCHEMA [bff] AUTHORIZATION [dbo];');

CREATE TABLE [bff].[Sessions] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [SessionKeyHash] binary(32) NOT NULL,          -- SHA-256(cookie session key)
    [SessionId] char(32) NOT NULL,                 -- public session id (logout CSRF token, token lookups)
    [Subject] nvarchar(200) NOT NULL,
    [TenantId] bigint NULL,
    [Ticket] varbinary(max) NOT NULL,              -- protected AuthenticationTicket (claims, no tokens)
    [Tokens] varbinary(max) NULL,                  -- protected access/refresh/id tokens
    [AccessTokenExpiresAtUtc] datetimeoffset(7) NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [RenewedAtUtc] datetimeoffset(7) NOT NULL,
    [ExpiresAtUtc] datetimeoffset(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Sessions] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Sessions_SessionKeyHash] UNIQUE NONCLUSTERED ([SessionKeyHash]),
    CONSTRAINT [UQ_Sessions_SessionId] UNIQUE NONCLUSTERED ([SessionId])
);

CREATE INDEX [IX_Sessions_ExpiresAtUtc] ON [bff].[Sessions] ([ExpiresAtUtc]);
CREATE INDEX [IX_Sessions_Subject] ON [bff].[Sessions] ([Subject]);
