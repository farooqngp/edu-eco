using EduEco.Api.Security.Authorization;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

[Route("api/v1/tenants")]
public sealed class TenantsController(ITenantQueries tenantQueries, ITenantContext tenantContext) : ApiControllerBase
{
    /// <summary>Profile of the tenant the access token is bound to.</summary>
    [HttpGet("current")]
    [HasPermission(Permissions.Tenants.Read)]
    [ProducesResponseType<TenantDetails>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantDetails>> GetCurrent(CancellationToken cancellationToken)
    {
        var tenant = await tenantQueries.FindByIdAsync(tenantContext.TenantId!.Value, cancellationToken);
        return tenant is null ? NotFound() : tenant;
    }

    /// <summary>All tenants (platform administration).</summary>
    [HttpGet]
    [HasPermission(Permissions.Tenants.Manage)]
    [ProducesResponseType<PagedResult<TenantDetails>>(StatusCodes.Status200OK)]
    public Task<PagedResult<TenantDetails>> List([FromQuery] PagingQuery paging, [FromQuery] string? search, CancellationToken cancellationToken) =>
        tenantQueries.ListAsync(search, paging.ToPageRequest(), cancellationToken);
}
