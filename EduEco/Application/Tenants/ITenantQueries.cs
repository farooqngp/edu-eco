namespace EduEco.Application.Tenants;

public sealed record TenantSummary(long Id, string Code, string Name);

public sealed record TenantMembershipSummary(long Id, string Code, string Name, bool IsDefault);

public sealed record TenantDetails(long Id, string Code, string Name, bool IsActive, DateTimeOffset CreatedAtUtc);

public interface ITenantQueries
{
    /// <summary>All tenants (platform administration), optional code/name prefix filter.</summary>
    Task<Abstractions.Persistence.PagedResult<TenantDetails>> ListAsync(
        string? search, Abstractions.Persistence.PageRequest page, CancellationToken cancellationToken = default);

    Task<TenantDetails?> FindByIdAsync(long tenantId, CancellationToken cancellationToken = default);

    /// <summary>Active tenants in which the user holds at least one membership.</summary>
    Task<IReadOnlyList<TenantMembershipSummary>> GetUserTenantsAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Active tenants, optionally filtered by code/name prefix (platform administrators).</summary>
    Task<IReadOnlyList<TenantSummary>> SearchActiveTenantsAsync(string? search, int take, CancellationToken cancellationToken = default);

    Task<TenantSummary?> FindActiveTenantByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<TenantSummary?> FindActiveTenantByIdAsync(long tenantId, CancellationToken cancellationToken = default);

    /// <summary>Role names the user holds inside the tenant (memberships only; global roles come from Identity).</summary>
    Task<IReadOnlyList<string>> GetTenantRoleNamesAsync(long userId, long tenantId, CancellationToken cancellationToken = default);
}
