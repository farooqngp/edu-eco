-- RunAlways, idempotent. Permission catalogue is code-owned; must mirror EduEco.Core.Authorization.Permissions (verified by tests).
-- Permissions removed from code are deleted (role links cascade).
SET NOCOUNT ON;
SET XACT_ABORT ON;

MERGE [auth].[Permissions] AS target
USING (VALUES
    (1, N'tenants.manage', N'Tenants', N'Create and manage tenants'),
    (2, N'users.manage', N'Users', N'Manage users and memberships within a tenant'),
    (3, N'courses.read', N'Courses', N'View courses'),
    (4, N'courses.write', N'Courses', N'Create and edit courses'),
    (5, N'grades.read', N'Grades', N'View grades'),
    (6, N'grades.write', N'Grades', N'Record and edit grades'),
    (7, N'tenants.read', N'Tenants', N'View the current tenant profile')
) AS source ([Id], [Name], [GroupName], [Description])
ON target.[Id] = source.[Id]
WHEN MATCHED AND (target.[Name] <> source.[Name] OR target.[GroupName] <> source.[GroupName] OR target.[Description] <> source.[Description]) THEN
    UPDATE SET [Name] = source.[Name], [GroupName] = source.[GroupName], [Description] = source.[Description]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Id], [Name], [GroupName], [Description])
    VALUES (source.[Id], source.[Name], source.[GroupName], source.[Description])
WHEN NOT MATCHED BY SOURCE THEN
    DELETE;
