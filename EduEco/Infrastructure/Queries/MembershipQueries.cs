using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Memberships;
using EduEco.Infrastructure.Persistence.Dapper;

namespace EduEco.Infrastructure.Queries;

public sealed class MembershipQueries(IQueryExecutor queryExecutor, ITenantContext tenantContext) : IMembershipQueries
{
    private const string Select = """
        SELECT m.Id, m.TenantId, m.UserId, u.Email, u.DisplayName, r.Name AS RoleName, m.IsDefault, m.CreatedAtUtc
        FROM auth.UserTenantMemberships m
        INNER JOIN auth.AspNetUsers u ON u.Id = m.UserId
        INNER JOIN auth.AspNetRoles r ON r.Id = m.RoleId
        """;

    public Task<PagedResult<MembershipDetails>> ListForCurrentTenantAsync(PageRequest page, CancellationToken cancellationToken = default) =>
        queryExecutor.QueryPagedAsync<MembershipDetails>(
            $"{Select} WHERE m.TenantId = @TenantId ORDER BY m.Id",
            "SELECT COUNT(*) FROM auth.UserTenantMemberships WHERE TenantId = @TenantId",
            TenantParameters.With(tenantContext),
            page,
            cancellationToken);

    public Task<MembershipDetails?> FindInCurrentTenantAsync(long membershipId, CancellationToken cancellationToken = default) =>
        queryExecutor.QuerySingleOrDefaultAsync<MembershipDetails>(
            $"{Select} WHERE m.Id = @Id AND m.TenantId = @TenantId",
            TenantParameters.With(tenantContext, new { Id = membershipId }),
            cancellationToken);

    public Task<bool> IsActiveUserAsync(long userId, CancellationToken cancellationToken = default) =>
        queryExecutor.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM auth.AspNetUsers WHERE Id = @Id AND IsActive = 1 AND EmailConfirmed = 1) THEN 1 ELSE 0 END",
            new { Id = userId },
            cancellationToken);
}
