-- ASP.NET Core Identity schema Version3: WebAuthn passkeys (IdentityUserPasskey<long>).
-- Baseline generated via `generate-auth-ddl`. CredentialId is random (authenticator-generated), so the PK is
-- NONCLUSTERED and the table is clustered on UserId (append-friendly, per-user lookups).
SET XACT_ABORT ON;

CREATE TABLE [auth].[AspNetUserPasskeys] (
    [CredentialId] varbinary(1024) NOT NULL,
    [UserId] bigint NOT NULL,
    [Data] nvarchar(max) NOT NULL,
    CONSTRAINT [PK_AspNetUserPasskeys] PRIMARY KEY NONCLUSTERED ([CredentialId]),
    CONSTRAINT [FK_AspNetUserPasskeys_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

CREATE CLUSTERED INDEX [IX_AspNetUserPasskeys_UserId] ON [auth].[AspNetUserPasskeys] ([UserId]);
