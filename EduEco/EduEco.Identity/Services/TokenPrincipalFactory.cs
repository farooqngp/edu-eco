using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using EduEco.Application.Tenants;
using EduEcoClaimTypes = EduEco.Core.Authorization.EduEcoClaimTypes;
using EduEco.Core.Identity;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EduEco.Identity.Services;

/// <summary>
/// Builds the claims principal OpenIddict serialises into tokens. Access tokens stay small (RFC 9068):
/// sub, tenant_id, role, scope, aud, client_id. Permissions are resolved server-side by the API, never embedded.
/// </summary>
public sealed class TokenPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    ITenantQueries tenantQueries,
    IOpenIddictScopeManager scopeManager)
{
    /// <summary>Private claim (no destination): detects password/2FA/passkey changes at refresh.</summary>
    public const string SecurityStampClaim = "eduEco_sstamp";

    /// <summary>Private claim (no destination): original sign-in time (Unix seconds) for the absolute refresh lifetime.</summary>
    public const string SessionStartedClaim = "eduEco_session_start";

    private const string AuthenticationType = "EduEco.Identity";

    public async Task<ClaimsPrincipal> CreateForUserAsync(
        ApplicationUser user,
        TenantSummary tenant,
        ImmutableArray<string> scopes,
        DateTimeOffset sessionStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);

        var globalRoles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
        var tenantRoles = await tenantQueries.GetTenantRoleNamesAsync(user.Id, tenant.Id, cancellationToken).ConfigureAwait(false);
        var roles = globalRoles.Concat(tenantRoles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

        var identity = new ClaimsIdentity(AuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString(CultureInfo.InvariantCulture))
            .SetClaim(Claims.Name, user.DisplayName ?? user.UserName)
            .SetClaim(Claims.PreferredUsername, user.UserName)
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, user.EmailConfirmed)
            .SetClaim(EduEcoClaimTypes.TenantId, tenant.Id.ToString(CultureInfo.InvariantCulture))
            .SetClaims(Claims.Role, roles)
            .SetClaim(SecurityStampClaim, await userManager.GetSecurityStampAsync(user).ConfigureAwait(false))
            .SetClaim(SessionStartedClaim, sessionStarted.ToUnixTimeSeconds());

        return await FinishAsync(identity, scopes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Principal for a platform administrator acting outside any tenant: no <c>tenant_id</c> claim and global roles
    /// only. Needed because a platform admin may have no tenant to select at all (provisioning the first one), and
    /// <c>tenants.manage</c> is declared <c>TenantScoped: false</c> precisely so it does not require one.
    /// <para>
    /// <c>offline_access</c> is stripped here rather than at the call site: this is the most privileged token shape
    /// in the system, so it is never refreshable and the session cannot be silently extended. Every tenant-scoped
    /// permission still fails <c>tenant_required</c> at the API, which keeps the token narrow.
    /// </para>
    /// </summary>
    public async Task<ClaimsPrincipal> CreateForPlatformAdminAsync(
        ApplicationUser user,
        ImmutableArray<string> scopes,
        DateTimeOffset sessionStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var roles = (await userManager.GetRolesAsync(user).ConfigureAwait(false))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

        var identity = new ClaimsIdentity(AuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString(CultureInfo.InvariantCulture))
            .SetClaim(Claims.Name, user.DisplayName ?? user.UserName)
            .SetClaim(Claims.PreferredUsername, user.UserName)
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, user.EmailConfirmed)
            .SetClaims(Claims.Role, roles)
            .SetClaim(SecurityStampClaim, await userManager.GetSecurityStampAsync(user).ConfigureAwait(false))
            .SetClaim(SessionStartedClaim, sessionStarted.ToUnixTimeSeconds());

        var withoutOfflineAccess = scopes
            .Where(s => !string.Equals(s, Scopes.OfflineAccess, StringComparison.Ordinal))
            .ToImmutableArray();

        return await FinishAsync(identity, withoutOfflineAccess, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClaimsPrincipal> CreateForClientAsync(
        string clientId,
        string? displayName,
        long? tenantId,
        ImmutableArray<string> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var identity = new ClaimsIdentity(AuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, clientId)
            .SetClaim(Claims.Name, displayName ?? clientId);

        if (tenantId is not null)
        {
            identity.SetClaim(EduEcoClaimTypes.TenantId, tenantId.Value.ToString(CultureInfo.InvariantCulture));
        }

        return await FinishAsync(identity, scopes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClaimsPrincipal> FinishAsync(ClaimsIdentity identity, ImmutableArray<string> scopes, CancellationToken cancellationToken)
    {
        identity.SetScopes(scopes);

        var resources = new List<string>();
        await foreach (var resource in scopeManager.ListResourcesAsync(scopes, cancellationToken).ConfigureAwait(false))
        {
            resources.Add(resource);
        }

        identity.SetResources(resources);
        identity.SetDestinations(GetDestinations);
        return new ClaimsPrincipal(identity);
    }

    internal static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Subject:
            case EduEcoClaimTypes.TenantId:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            case Claims.Role:
            case "act": // RFC 8693 delegation chain
            case "cnf": // RFC 9449 key binding
                yield return Destinations.AccessToken;
                yield break;

            case Claims.Name or Claims.PreferredUsername when claim.Subject?.HasScope(Scopes.Profile) == true:
                yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email or Claims.EmailVerified when claim.Subject?.HasScope(Scopes.Email) == true:
                yield return Destinations.IdentityToken;
                yield break;

            // Security stamp, session start and anything unknown never leave the server
            // (kept only inside authorization codes / refresh tokens).
            default:
                yield break;
        }
    }
}
