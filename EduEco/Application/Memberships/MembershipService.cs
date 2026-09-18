using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Common;
using EduEco.Core.Authorization;

namespace EduEco.Application.Memberships;

public sealed record CreateMembershipCommand(long UserId, string RoleName, bool IsDefault);

/// <summary>Tenant membership administration (requires <c>users.manage</c> at the API boundary).</summary>
public sealed class MembershipService(
    ICommandRepository<UserTenantMembership> memberships,
    IMembershipQueries membershipQueries,
    ICurrentUser currentUser,
    IPermissionCacheInvalidator permissionCache)
{
    /// <summary>Roles assignable through tenant memberships. PlatformAdmin is global and never tenant-assigned.</summary>
    public static readonly IReadOnlySet<string> AssignableRoles =
        new HashSet<string>(StringComparer.Ordinal) { Roles.TenantAdmin, Roles.Teacher, Roles.Student, Roles.Parent };

    public async Task<Result<MembershipDetails>> CreateAsync(CreateMembershipCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!AssignableRoles.Contains(command.RoleName))
        {
            return Result<MembershipDetails>.Fail(ResultError.Validation, $"Role '{command.RoleName}' cannot be assigned to a tenant membership.");
        }

        if (!await membershipQueries.IsActiveUserAsync(command.UserId, cancellationToken).ConfigureAwait(false))
        {
            return Result<MembershipDetails>.Fail(ResultError.Validation, "The user does not exist or is inactive.");
        }

        var membership = new UserTenantMembership
        {
            UserId = command.UserId,
            RoleId = Roles.All.Single(r => r.Name == command.RoleName).Id,
            IsDefault = command.IsDefault,
        };

        try
        {
            // TenantId is stamped from the caller's tenant context by the repository.
            await memberships.InsertAsync(membership, cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateEntityException)
        {
            return Result<MembershipDetails>.Fail(ResultError.Conflict, "The user already holds this role in the tenant.");
        }

        await permissionCache.InvalidateUserAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        var created = await membershipQueries.FindInCurrentTenantAsync(membership.Id, cancellationToken).ConfigureAwait(false);
        return Result<MembershipDetails>.Ok(created!);
    }

    public async Task<Result<bool>> DeleteAsync(long membershipId, CancellationToken cancellationToken)
    {
        var existing = await memberships.GetByIdAsync(membershipId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return Result<bool>.Fail(ResultError.NotFound, "Membership not found.");
        }

        if (existing.UserId == currentUser.UserId)
        {
            return Result<bool>.Fail(ResultError.Validation, "You cannot remove your own membership.");
        }

        if (!await memberships.DeleteAsync(membershipId, cancellationToken).ConfigureAwait(false))
        {
            return Result<bool>.Fail(ResultError.NotFound, "Membership not found.");
        }

        // Revocation takes effect on the user's next API call (their access token may still be valid for minutes).
        await permissionCache.InvalidateUserAsync(existing.UserId, cancellationToken).ConfigureAwait(false);
        return Result<bool>.Ok(true);
    }
}
