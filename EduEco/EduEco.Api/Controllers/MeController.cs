using EduEco.Api.Security;
using EduEco.Application.Abstractions.Security;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

public sealed record CallerResponse(
    string? Subject,
    long? UserId,
    string? ClientId,
    bool IsServiceClient,
    long? TenantId,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> EffectivePermissions);

/// <summary>Introspects the caller (lets clients adapt UI to effective permissions).</summary>
[Route("api/v1/me")]
[Authorize]
public sealed class MeController(IPermissionService permissionService) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<CallerResponse>(StatusCodes.Status200OK)]
    public async Task<CallerResponse> Get(CancellationToken cancellationToken)
    {
        var scopes = User.GetScopes();
        var tenantId = User.GetTenantId();
        var userId = User.GetUserId();

        IEnumerable<PermissionDefinition> candidates = Permissions.All
            .Where(p => scopes.Contains(p.RequiredScope) && (!p.TenantScoped || tenantId is not null));

        if (User.IsServiceClient())
        {
            candidates = candidates.Where(p => p.AllowServiceClients);
        }
        else if (userId is not null && tenantId is not null)
        {
            var granted = await permissionService.GetPermissionsAsync(userId.Value, tenantId.Value, cancellationToken);
            candidates = candidates.Where(p => granted.Contains(p.Name));
        }
        else
        {
            candidates = [];
        }

        return new CallerResponse(
            User.GetSubject(),
            userId,
            User.GetClientId(),
            User.IsServiceClient(),
            tenantId,
            [.. scopes.Order(StringComparer.Ordinal)],
            [.. User.FindAll(ClaimsPrincipalExtensions.RoleClaim).Select(c => c.Value).Order(StringComparer.Ordinal)],
            [.. candidates.Select(p => p.Name).Order(StringComparer.Ordinal)]);
    }
}
