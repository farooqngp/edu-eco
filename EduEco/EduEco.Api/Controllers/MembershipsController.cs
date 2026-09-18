using System.ComponentModel.DataAnnotations;
using EduEco.Api.Security;
using EduEco.Api.Security.Authorization;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Memberships;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

public sealed record CreateMembershipRequest(
    [Range(1, long.MaxValue)] long UserId,
    [Required, StringLength(64)] string Role,
    bool IsDefault = false);

public sealed record MembershipResponse(
    long Id,
    long TenantId,
    long UserId,
    string? Email,
    string? DisplayName,
    string Role,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc) : ITenantResource
{
    public static MembershipResponse From(MembershipDetails m) =>
        new(m.Id, m.TenantId, m.UserId, m.Email, m.DisplayName, m.RoleName, m.IsDefault, m.CreatedAtUtc);
}

/// <summary>Role memberships inside the caller's tenant. The tenant always comes from the token, never from the request.</summary>
[Route("api/v1/memberships")]
[HasPermission(Permissions.Users.Manage)]
public sealed class MembershipsController(
    IMembershipQueries membershipQueries,
    MembershipService membershipService,
    IAuthorizationService authorizationService) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<MembershipResponse>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<MembershipResponse>> List([FromQuery] PagingQuery paging, CancellationToken cancellationToken)
    {
        var page = await membershipQueries.ListForCurrentTenantAsync(paging.ToPageRequest(), cancellationToken);
        return new PagedResult<MembershipResponse>([.. page.Items.Select(MembershipResponse.From)], page.PageNumber, page.PageSize, page.TotalCount);
    }

    [HttpGet("{id:long}", Name = "GetMembership")]
    [ProducesResponseType<MembershipResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MembershipResponse>> Get(long id, CancellationToken cancellationToken)
    {
        var membership = await membershipQueries.FindInCurrentTenantAsync(id, cancellationToken);
        if (membership is null)
        {
            return NotFound();
        }

        var response = MembershipResponse.From(membership);

        // Defence in depth (BOLA): the query is tenant-filtered and the resource is re-checked. Foreign resources
        // are reported as 404, never 403, so their existence is not disclosed.
        var authorization = await authorizationService.AuthorizeAsync(User, response, SameTenantRequirement.Instance);
        return authorization.Succeeded ? response : NotFound();
    }

    [HttpPost]
    [RequireActiveToken]
    // Bearer-only API (no cookie auth, see ApiSetup.AddAuthentication): a browser cannot attach the
    // Authorization header automatically, so CSRF forgery isn't possible here.
    [IgnoreAntiforgeryToken]
    [ProducesResponseType<MembershipResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MembershipResponse>> Create(CreateMembershipRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await membershipService.CreateAsync(new CreateMembershipCommand(request.UserId, request.Role, request.IsDefault), cancellationToken);
        return result.Succeeded
            ? CreatedAtRoute("GetMembership", new { id = result.Value!.Id }, MembershipResponse.From(result.Value))
            : ProblemFor(result);
    }

    [HttpDelete("{id:long}")]
    [RequireActiveToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await membershipService.DeleteAsync(id, cancellationToken);
        return result.Succeeded ? NoContent() : ProblemFor(result);
    }
}
