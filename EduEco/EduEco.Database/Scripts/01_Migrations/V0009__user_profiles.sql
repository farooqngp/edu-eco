-- Personal profile data (name/contact beyond DisplayName/email), one row per user, independent of tenant membership.
-- Dapper entity: EduEco.Core.Identity.UserProfile.
SET XACT_ABORT ON;

CREATE TABLE [dbo].[UserProfiles] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [UserId] bigint NOT NULL,
    [DateOfBirth] date NULL,
    [Address] nvarchar(200) NULL,
    [City] nvarchar(100) NULL,
    [PostalCode] nvarchar(20) NULL,
    [Country] nvarchar(100) NULL,
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [CreatedBy] nvarchar(256) NOT NULL,
    [UpdatedAtUtc] datetimeoffset(7) NULL,
    [UpdatedBy] nvarchar(256) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_UserProfiles] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_UserProfiles_UserId] UNIQUE NONCLUSTERED ([UserId]),
    CONSTRAINT [FK_UserProfiles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [auth].[AspNetUsers] ([Id]) ON DELETE CASCADE
);
