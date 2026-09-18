using EduEco.Application.Abstractions.Persistence;

namespace EduEco.Application.Memberships;

public sealed record MembershipDetails(
    long Id,
    long TenantId,
    long UserId,
    string? Email,
    string? DisplayName,
    string RoleName,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc);

public interface IMembershipQueries
{
    /// <summary>Memberships of the current tenant (tenant taken from <c>ITenantContext</c>, never from input).</summary>
    Task<PagedResult<MembershipDetails>> ListForCurrentTenantAsync(PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Single membership of the current tenant; <c>null</c> when absent or owned by another tenant.</summary>
    Task<MembershipDetails?> FindInCurrentTenantAsync(long membershipId, CancellationToken cancellationToken = default);

    Task<bool> IsActiveUserAsync(long userId, CancellationToken cancellationToken = default);
}
