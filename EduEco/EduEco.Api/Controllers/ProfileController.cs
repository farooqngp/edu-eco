using System.ComponentModel.DataAnnotations;
using EduEco.Api.Security;
using EduEco.Application.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

public sealed record ProfileResponse(
    long UserId,
    DateOnly? DateOfBirth,
    string? Address,
    string? City,
    string? PostalCode,
    string? Country,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc)
{
    public static ProfileResponse From(ProfileDetails p) =>
        new(p.UserId, p.DateOfBirth, p.Address, p.City, p.PostalCode, p.Country, p.CreatedAtUtc, p.UpdatedAtUtc);
}

public sealed record UpdateProfileRequest(
    DateOnly? DateOfBirth,
    [StringLength(200)] string? Address,
    [StringLength(100)] string? City,
    [StringLength(20)] string? PostalCode,
    [StringLength(100)] string? Country);

/// <summary>The caller's own profile (rest of their info beyond DisplayName/email) — one row per user, not tenant-scoped.</summary>
[Route("api/v1/profile")]
[Authorize]
public sealed class ProfileController(ProfileService profileService) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<ProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileResponse>> Get(CancellationToken cancellationToken)
    {
        var result = await profileService.GetAsync(cancellationToken);
        return result.Succeeded ? ProfileResponse.From(result.Value!) : ProblemFor(result);
    }

    [HttpPut]
    [RequireActiveToken]
    // Bearer-only API (no cookie auth, see ApiSetup.AddAuthentication): CSRF forgery isn't possible here.
    [IgnoreAntiforgeryToken]
    [ProducesResponseType<ProfileResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProfileResponse>> Update(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await profileService.UpsertAsync(
            new UpsertProfileCommand(request.DateOfBirth, request.Address, request.City, request.PostalCode, request.Country),
            cancellationToken);
        return result.Succeeded ? ProfileResponse.From(result.Value!) : ProblemFor(result);
    }
}
