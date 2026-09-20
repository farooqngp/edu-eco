using System.ComponentModel.DataAnnotations;
using EduEco.Api.Security;
using EduEco.Api.Security.Authorization;
using EduEco.Api.Tenants;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

public sealed record CreateTenantRequest(
    [Required, StringLength(50, MinimumLength = 2)] string Code,
    [Required, StringLength(200, MinimumLength = 2)] string Name,
    [Required, EmailAddress, StringLength(256)] string AdminEmail,
    [Required, StringLength(100, MinimumLength = 2)] string AdminDisplayName);

/// <summary>No credential is ever returned: the administrator sets their own password from the emailed link.</summary>
public sealed record TenantProvisionedResponse(long Id, string Code, string Name, long AdminUserId, bool InvitationSent)
{
    public static TenantProvisionedResponse From(TenantProvisioned provisioned) =>
        new(provisioned.TenantId, provisioned.Code, provisioned.Name, provisioned.AdminUserId, provisioned.InvitationSent);
}

[Route("api/v1/tenants")]
public sealed class TenantsController(
    ITenantQueries tenantQueries,
    ITenantContext tenantContext,
    TenantProvisioningService provisioningService) : ApiControllerBase
{
    /// <summary>Profile of the tenant the access token is bound to.</summary>
    [HttpGet("current")]
    [HasPermission(Permissions.Tenants.Read)]
    [ProducesResponseType<TenantDetails>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantDetails>> GetCurrent(CancellationToken cancellationToken)
    {
        // Defence in depth: Tenants.Read is TenantScoped, so a tenant-less token is already denied upstream
        // (tenant_required) — but never dereference the claim on the assumption that it must be there.
        if (tenantContext.TenantId is null)
        {
            return Problem(detail: "The token is not bound to a tenant.", statusCode: StatusCodes.Status400BadRequest);
        }

        var tenant = await tenantQueries.FindByIdAsync(tenantContext.TenantId.Value, cancellationToken);
        return tenant is null ? NotFound() : tenant;
    }

    /// <summary>All tenants (platform administration).</summary>
    [HttpGet]
    [HasPermission(Permissions.Tenants.Manage)]
    [ProducesResponseType<PagedResult<TenantDetails>>(StatusCodes.Status200OK)]
    public Task<PagedResult<TenantDetails>> List([FromQuery] PagingQuery paging, [FromQuery] string? search, CancellationToken cancellationToken) =>
        tenantQueries.ListAsync(search, paging.ToPageRequest(), cancellationToken);

    [HttpGet("{id:long}", Name = "GetTenant")]
    [HasPermission(Permissions.Tenants.Manage)]
    [ProducesResponseType<TenantDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDetails>> GetById(long id, CancellationToken cancellationToken)
    {
        var tenant = await tenantQueries.FindByIdAsync(id, cancellationToken);
        return tenant is null ? NotFound() : tenant;
    }

    /// <summary>
    /// Registers a tenant and provisions its administrator, who is emailed a link to set their own password.
    /// <c>tenants.manage</c> is not tenant-scoped, so a platform admin can call this with no tenant of their own —
    /// which is what makes provisioning the very first tenant possible.
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.Tenants.Manage)]
    [RequireActiveToken]
    // Bearer-only API (no cookie auth, see ApiSetup.AddAuthentication): a browser cannot attach the
    // Authorization header automatically, so CSRF forgery isn't possible here.
    [IgnoreAntiforgeryToken]
    [ProducesResponseType<TenantProvisionedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantProvisionedResponse>> Create(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await provisioningService.ProvisionAsync(
            new ProvisionTenantCommand(request.Code, request.Name, request.AdminEmail, request.AdminDisplayName),
            cancellationToken);

        return result.Succeeded
            ? CreatedAtRoute("GetTenant", new { id = result.Value!.TenantId }, TenantProvisionedResponse.From(result.Value))
            : ProblemFor(result);
    }
}
