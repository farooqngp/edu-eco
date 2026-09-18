using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Authorization;

namespace EduEco.Infrastructure.Queries;

public sealed class PermissionQueries(IQueryExecutor queryExecutor) : IPermissionQueries
{
    private const string Sql = """
        SELECT DISTINCT p.[Name]
        FROM auth.Permissions p
        INNER JOIN auth.RolePermissions rp ON rp.PermissionId = p.Id
        WHERE EXISTS (SELECT 1 FROM auth.AspNetUsers u WHERE u.Id = @UserId AND u.IsActive = 1)
          AND rp.RoleId IN (
                SELECT m.RoleId
                FROM auth.UserTenantMemberships m
                INNER JOIN dbo.Tenants t ON t.Id = m.TenantId AND t.IsActive = 1
                WHERE m.UserId = @UserId AND m.TenantId = @TenantId
                UNION
                SELECT ur.RoleId
                FROM auth.AspNetUserRoles ur
                WHERE ur.UserId = @UserId)
        ORDER BY p.[Name];
        """;

    public Task<IReadOnlyList<string>> GetPermissionNamesAsync(long userId, long tenantId, CancellationToken cancellationToken = default) =>
        queryExecutor.QueryAsync<string>(Sql, new { UserId = userId, TenantId = tenantId }, cancellationToken);
}
