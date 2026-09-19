using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Common;
using EduEco.Core.Identity;

namespace EduEco.Application.Profiles;

public sealed record ProfileDetails(
    long UserId,
    DateOnly? DateOfBirth,
    string? Address,
    string? City,
    string? PostalCode,
    string? Country,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc)
{
    public static ProfileDetails From(UserProfile p) =>
        new(p.UserId, p.DateOfBirth, p.Address, p.City, p.PostalCode, p.Country, p.CreatedAtUtc, p.UpdatedAtUtc);
}

public sealed record UpsertProfileCommand(DateOnly? DateOfBirth, string? Address, string? City, string? PostalCode, string? Country);

/// <summary>Always acts on the caller's own profile (<see cref="ICurrentUser.UserId"/>) — never a caller-supplied id (BOLA).</summary>
public sealed class ProfileService(ICommandRepository<UserProfile> profiles, IProfileQueries profileQueries, ICurrentUser currentUser)
{
    public async Task<Result<ProfileDetails>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result<ProfileDetails>.Fail(ResultError.Forbidden, "Only an authenticated user has a profile.");
        }

        var profile = await profileQueries.FindByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return profile is null
            ? Result<ProfileDetails>.Fail(ResultError.NotFound, "No profile yet.")
            : Result<ProfileDetails>.Ok(ProfileDetails.From(profile));
    }

    public async Task<Result<ProfileDetails>> UpsertAsync(UpsertProfileCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (currentUser.UserId is not { } userId)
        {
            return Result<ProfileDetails>.Fail(ResultError.Forbidden, "Only an authenticated user has a profile.");
        }

        var existing = await profileQueries.FindByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        var profile = existing ?? new UserProfile { UserId = userId };
        profile.DateOfBirth = command.DateOfBirth;
        profile.Address = command.Address;
        profile.City = command.City;
        profile.PostalCode = command.PostalCode;
        profile.Country = command.Country;

        if (existing is null)
        {
            await profiles.InsertAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await profiles.UpdateAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        return Result<ProfileDetails>.Ok(ProfileDetails.From(profile));
    }
}
