-- RunAlways, idempotent. System roles; must mirror EduEco.Core.Authorization.Roles (verified by tests).
-- System role ids are fixed (1-999); custom roles receive IDENTITY values >= 1000.
SET NOCOUNT ON;
SET XACT_ABORT ON;

SET IDENTITY_INSERT [auth].[AspNetRoles] ON;

MERGE [auth].[AspNetRoles] AS target
USING (VALUES
    (1, N'PlatformAdmin', N'Platform-wide administrator'),
    (2, N'TenantAdmin', N'School or district administrator'),
    (3, N'Teacher', N'Teacher'),
    (4, N'Student', N'Student'),
    (5, N'Parent', N'Parent or guardian')
) AS source ([Id], [Name], [Description])
ON target.[Id] = source.[Id]
WHEN MATCHED AND (target.[Name] <> source.[Name] OR ISNULL(target.[Description], N'') <> source.[Description] OR target.[IsSystem] = 0) THEN
    UPDATE SET
        [Name] = source.[Name],
        [NormalizedName] = UPPER(source.[Name]),
        [Description] = source.[Description],
        [IsSystem] = 1,
        [ConcurrencyStamp] = CONVERT(nvarchar(36), NEWID())
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Id], [Name], [NormalizedName], [Description], [IsSystem], [ConcurrencyStamp])
    VALUES (source.[Id], source.[Name], UPPER(source.[Name]), source.[Description], 1, CONVERT(nvarchar(36), NEWID()));

SET IDENTITY_INSERT [auth].[AspNetRoles] OFF;

-- Guarantee the reserved range: next generated id is always >= 1000.
IF ISNULL(IDENT_CURRENT(N'auth.AspNetRoles'), 0) < 999
    DBCC CHECKIDENT (N'auth.AspNetRoles', RESEED, 999) WITH NO_INFOMSGS;
