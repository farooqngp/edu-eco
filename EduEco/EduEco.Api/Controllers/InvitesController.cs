using System.ComponentModel.DataAnnotations;
using EduEco.Api.Security;
using EduEco.Api.Security.Authorization;
using EduEco.Application.Invites;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

public sealed record CreateInviteRequest(
    [Required] string RoleName,
    [Required] DateTimeOffset ExpiresAtUtc,
    [Required, EmailAddress, StringLength(256)] string Email);

public sealed record InviteResponse(long InviteId, string Code, DateTimeOffset ExpiresAtUtc)
{
    public static InviteResponse From(InviteIssued issued) => new(issued.InviteId, issued.Code, issued.ExpiresAtUtc);
}

/// <summary>Tenant admins invite new members; the code is shown exactly once in this response, never persisted in plaintext.</summary>
[Route("api/v1/invites")]
[HasPermission(Permissions.Users.Manage)]
public sealed class InvitesController(InviteService inviteService) : ApiControllerBase
{
    [HttpPost]
    [RequireActiveToken]
    // Bearer-only API (no cookie auth, see ApiSetup.AddAuthentication): a browser cannot attach the
    // Authorization header automatically, so CSRF forgery isn't possible here.
    [IgnoreAntiforgeryToken]
    [ProducesResponseType<InviteResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InviteResponse>> Create(CreateInviteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await inviteService.IssueAsync(new CreateInviteCommand(request.RoleName, request.ExpiresAtUtc, request.Email), cancellationToken);
        return result.Succeeded
            ? StatusCode(StatusCodes.Status201Created, InviteResponse.From(result.Value!))
            : ProblemFor(result);
    }
}
