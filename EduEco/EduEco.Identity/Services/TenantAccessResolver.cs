using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using EduEco.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace EduEco.Identity.Services;

public enum TenantSelectionKind
{
    Selected,
    SelectionRequired,
    Denied,
}

public sealed record TenantSelection(TenantSelectionKind Kind, TenantSummary? Tenant = null);

/// <summary>
/// Decides which tenant a token is issued for. One tenant per token: switching tenant requires a new authorization request.
/// Platform administrators may act for any active tenant; everyone else only for tenants they are members of.
/// </summary>
public sealed class TenantAccessResolver(ITenantQueries tenantQueries, UserManager<ApplicationUser> userManager)
{
    public const int MaxSelectableTenants = 50;

    public Task<bool> IsPlatformAdminAsync(ApplicationUser user) => userManager.IsInRoleAsync(user, Roles.PlatformAdmin);

    public async Task<TenantSelection> SelectAsync(ApplicationUser user, string? requestedTenantCode, CancellationToken cancellationToken)
    {
        var isPlatformAdmin = await IsPlatformAdminAsync(user).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(requestedTenantCode))
        {
            var tenant = await FindAccessibleByCodeAsync(user, requestedTenantCode.Trim(), isPlatformAdmin, cancellationToken).ConfigureAwait(false);
            return tenant is null ? new(TenantSelectionKind.Denied) : new(TenantSelectionKind.Selected, tenant);
        }

        if (isPlatformAdmin)
        {
            return new(TenantSelectionKind.SelectionRequired);
        }

        var memberships = await tenantQueries.GetUserTenantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return memberships.Count switch
        {
            0 => new(TenantSelectionKind.Denied),
            1 => new(TenantSelectionKind.Selected, ToSummary(memberships[0])),
            _ => new(TenantSelectionKind.SelectionRequired),
        };
    }

    /// <summary>Re-validates tenant access at token redemption/refresh (memberships may have been revoked).</summary>
    public async Task<TenantSummary?> FindAccessibleByIdAsync(ApplicationUser user, long tenantId, CancellationToken cancellationToken)
    {
        if (await IsPlatformAdminAsync(user).ConfigureAwait(false))
        {
            return await tenantQueries.FindActiveTenantByIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
        }

        var memberships = await tenantQueries.GetUserTenantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return memberships.Where(m => m.Id == tenantId).Select(ToSummary).FirstOrDefault();
    }

    public async Task<IReadOnlyList<TenantMembershipSummary>> GetSelectableTenantsAsync(
        ApplicationUser user, string? search, CancellationToken cancellationToken)
    {
        if (await IsPlatformAdminAsync(user).ConfigureAwait(false))
        {
            var tenants = await tenantQueries.SearchActiveTenantsAsync(search, MaxSelectableTenants, cancellationToken).ConfigureAwait(false);
            return [.. tenants.Select(t => new TenantMembershipSummary(t.Id, t.Code, t.Name, IsDefault: false))];
        }

        return await tenantQueries.GetUserTenantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TenantSummary?> FindAccessibleByCodeAsync(
        ApplicationUser user, string code, bool isPlatformAdmin, CancellationToken cancellationToken)
    {
        if (isPlatformAdmin)
        {
            return await tenantQueries.FindActiveTenantByCodeAsync(code, cancellationToken).ConfigureAwait(false);
        }

        var memberships = await tenantQueries.GetUserTenantsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return memberships
            .Where(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase))
            .Select(ToSummary)
            .FirstOrDefault();
    }

    private static TenantSummary ToSummary(TenantMembershipSummary membership) => new(membership.Id, membership.Code, membership.Name);
}
