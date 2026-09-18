-- RunAlways, idempotent. Role-permission matrix for system roles; must mirror EduEco.Core.Authorization.Roles (verified by tests).
-- Only rows of system roles are managed; custom (tenant-defined) roles are untouched.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Matrix TABLE ([RoleId] bigint NOT NULL, [PermissionId] bigint NOT NULL, PRIMARY KEY ([RoleId], [PermissionId]));

-- (RoleId, PermissionId)
INSERT INTO @Matrix ([RoleId], [PermissionId])
VALUES
    -- 1 PlatformAdmin
    (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (1, 7),
    -- 2 TenantAdmin
    (2, 2), (2, 3), (2, 4), (2, 5), (2, 6), (2, 7),
    -- 3 Teacher
    (3, 3), (3, 4), (3, 5), (3, 6), (3, 7),
    -- 4 Student
    (4, 3), (4, 5), (4, 7),
    -- 5 Parent
    (5, 3), (5, 5), (5, 7);

DELETE rp
FROM [auth].[RolePermissions] rp
INNER JOIN [auth].[AspNetRoles] r ON r.[Id] = rp.[RoleId] AND r.[IsSystem] = 1
WHERE NOT EXISTS (SELECT 1 FROM @Matrix m WHERE m.[RoleId] = rp.[RoleId] AND m.[PermissionId] = rp.[PermissionId]);

INSERT INTO [auth].[RolePermissions] ([RoleId], [PermissionId])
SELECT m.[RoleId], m.[PermissionId]
FROM @Matrix m
WHERE NOT EXISTS (SELECT 1 FROM [auth].[RolePermissions] rp WHERE rp.[RoleId] = m.[RoleId] AND rp.[PermissionId] = m.[PermissionId]);
