using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Tenants;

namespace EduEco.Infrastructure.Queries;

public sealed class TenantQueries(IQueryExecutor queryExecutor) : ITenantQueries
{
    public Task<PagedResult<TenantDetails>> ListAsync(string? search, PageRequest page, CancellationToken cancellationToken = default)
    {
        const string filter = "WHERE (@Search IS NULL OR Code LIKE @Search + '%' OR Name LIKE @Search + '%')";
        return queryExecutor.QueryPagedAsync<TenantDetails>(
            $"SELECT Id, Code, Name, IsActive, CreatedAtUtc FROM dbo.Tenants {filter} ORDER BY Name, Id",
            $"SELECT COUNT(*) FROM dbo.Tenants {filter}",
            new { Search = string.IsNullOrWhiteSpace(search) ? null : EscapeLike(search.Trim()) },
            page,
            cancellationToken);
    }

    public Task<TenantDetails?> FindByIdAsync(long tenantId, CancellationToken cancellationToken = default) =>
        queryExecutor.QuerySingleOrDefaultAsync<TenantDetails>(
            "SELECT Id, Code, Name, IsActive, CreatedAtUtc FROM dbo.Tenants WHERE Id = @Id;",
            new { Id = tenantId },
            cancellationToken);

    public Task<IReadOnlyList<TenantMembershipSummary>> GetUserTenantsAsync(long userId, CancellationToken cancellationToken = default) =>
        queryExecutor.QueryAsync<TenantMembershipSummary>(
            """
            SELECT t.Id, t.Code, t.Name, CAST(MAX(CAST(m.IsDefault AS int)) AS bit) AS IsDefault
            FROM auth.UserTenantMemberships m
            INNER JOIN dbo.Tenants t ON t.Id = m.TenantId AND t.IsActive = 1
            WHERE m.UserId = @UserId
            GROUP BY t.Id, t.Code, t.Name
            ORDER BY t.Name;
            """,
            new { UserId = userId },
            cancellationToken);

    public Task<IReadOnlyList<TenantSummary>> SearchActiveTenantsAsync(string? search, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, 200);

        return queryExecutor.QueryAsync<TenantSummary>(
            """
            SELECT TOP (@Take) Id, Code, Name
            FROM dbo.Tenants
            WHERE IsActive = 1
              AND (@Search IS NULL OR Code LIKE @Search + '%' OR Name LIKE @Search + '%')
            ORDER BY Name;
            """,
            new { Take = take, Search = string.IsNullOrWhiteSpace(search) ? null : EscapeLike(search.Trim()) },
            cancellationToken);
    }

    public Task<TenantSummary?> FindActiveTenantByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        queryExecutor.QuerySingleOrDefaultAsync<TenantSummary>(
            "SELECT Id, Code, Name FROM dbo.Tenants WHERE Code = @Code AND IsActive = 1;",
            new { Code = code },
            cancellationToken);

    public Task<TenantSummary?> FindActiveTenantByIdAsync(long tenantId, CancellationToken cancellationToken = default) =>
        queryExecutor.QuerySingleOrDefaultAsync<TenantSummary>(
            "SELECT Id, Code, Name FROM dbo.Tenants WHERE Id = @Id AND IsActive = 1;",
            new { Id = tenantId },
            cancellationToken);

    public Task<IReadOnlyList<string>> GetTenantRoleNamesAsync(long userId, long tenantId, CancellationToken cancellationToken = default) =>
        queryExecutor.QueryAsync<string>(
            """
            SELECT DISTINCT r.Name
            FROM auth.UserTenantMemberships m
            INNER JOIN auth.AspNetRoles r ON r.Id = m.RoleId
            INNER JOIN dbo.Tenants t ON t.Id = m.TenantId AND t.IsActive = 1
            WHERE m.UserId = @UserId AND m.TenantId = @TenantId;
            """,
            new { UserId = userId, TenantId = tenantId },
            cancellationToken);

    private static string EscapeLike(string value) =>
        value.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
}
