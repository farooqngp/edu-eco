using System.Security.Cryptography;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Common;
using EduEco.Application.Memberships;
using EduEco.Core.Authorization;

namespace EduEco.Application.Invites;

public sealed record CreateInviteCommand(string RoleName, DateTimeOffset ExpiresAtUtc, string Email, int MaxUses = 1);

public sealed record InviteIssued(long InviteId, string Code, DateTimeOffset ExpiresAtUtc);

public sealed record InviteRedemption(long InviteId, long TenantId, string RoleName);

/// <summary>
/// Tenant invite codes: a tenant admin issues one (<see cref="IssueAsync"/>), a would-be member presents it at
/// self-registration. <see cref="ValidateAsync"/> is a read-only check (no ambient tenant required — see
/// <see cref="IInviteQueries"/>); <see cref="ConsumeAsync"/> marks it redeemed and must be called with a tenant
/// context matching <see cref="InviteRedemption.TenantId"/> (or cross-tenant), since it writes through the normal
/// tenant-scoped <see cref="ICommandRepository{T}"/> — there is no ambient tenant at anonymous registration time.
/// </summary>
public sealed class InviteService(
    ICommandRepository<TenantInvite> invites,
    IInviteQueries inviteQueries,
    IInviteEmailSender inviteEmailSender,
    TimeProvider timeProvider)
{
    /// <summary>Same roles a tenant admin can grant directly via membership creation.</summary>
    public static readonly IReadOnlySet<string> AssignableRoles = MembershipService.AssignableRoles;

    public async Task<Result<InviteIssued>> IssueAsync(CreateInviteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!AssignableRoles.Contains(command.RoleName))
        {
            return Result<InviteIssued>.Fail(ResultError.Validation, $"Role '{command.RoleName}' cannot be granted by an invite.");
        }

        if (command.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Result<InviteIssued>.Fail(ResultError.Validation, "ExpiresAtUtc must be in the future.");
        }

        if (command.MaxUses < 1)
        {
            return Result<InviteIssued>.Fail(ResultError.Validation, "MaxUses must be at least 1.");
        }

        var code = RandomNumberGenerator.GetHexString(20, lowercase: true);
        var invite = new TenantInvite
        {
            CodeHash = Hash(code),
            RoleId = Roles.All.Single(r => r.Name == command.RoleName).Id,
            InviteeEmail = command.Email,
            ExpiresAtUtc = command.ExpiresAtUtc,
            MaxUses = command.MaxUses,
        };

        try
        {
            await invites.InsertAsync(invite, cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateEntityException)
        {
            // A colliding 80-bit code hash is astronomically unlikely; treat it as a transient failure to retry.
            return Result<InviteIssued>.Fail(ResultError.Conflict, "Could not generate a unique invite code; try again.");
        }

        // Best-effort by contract (see IInviteEmailSender): never throws, so a delivery failure cannot undo issuance.
        await inviteEmailSender.SendInviteAsync(command.Email, code, command.RoleName, invite.ExpiresAtUtc, cancellationToken).ConfigureAwait(false);

        return Result<InviteIssued>.Ok(new InviteIssued(invite.Id, code, invite.ExpiresAtUtc));
    }

    public async Task<Result<InviteRedemption>> ValidateAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var invite = await inviteQueries.FindByCodeHashAsync(Hash(code), cancellationToken).ConfigureAwait(false);
        if (invite is null)
        {
            return Result<InviteRedemption>.Fail(ResultError.NotFound, "Invalid invite code.");
        }

        if (invite.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Result<InviteRedemption>.Fail(ResultError.Validation, "This invite has expired.");
        }

        if (invite.UseCount >= invite.MaxUses)
        {
            return Result<InviteRedemption>.Fail(ResultError.Conflict, "This invite has already been used.");
        }

        return Result<InviteRedemption>.Ok(new InviteRedemption(invite.Id, invite.TenantId, invite.RoleName));
    }

    /// <summary>Marks the invite redeemed. Call only after <see cref="ValidateAsync"/> succeeded for the same code.</summary>
    public async Task<Result<bool>> ConsumeAsync(long inviteId, long userId, CancellationToken cancellationToken = default)
    {
        var invite = await invites.GetByIdAsync(inviteId, cancellationToken).ConfigureAwait(false);
        if (invite is null)
        {
            return Result<bool>.Fail(ResultError.NotFound, "Invite not found.");
        }

        if (invite.UseCount >= invite.MaxUses || invite.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Result<bool>.Fail(ResultError.Conflict, "This invite has already been used or has expired.");
        }

        invite.UseCount++;
        invite.RedeemedByUserId = userId;
        invite.RedeemedAtUtc = timeProvider.GetUtcNow();

        try
        {
            var updated = await invites.UpdateAsync(invite, cancellationToken).ConfigureAwait(false);
            return updated ? Result<bool>.Ok(true) : Result<bool>.Fail(ResultError.NotFound, "Invite not found.");
        }
        catch (ConcurrencyException)
        {
            // Lost the race to another concurrent redemption of the same single-use code.
            return Result<bool>.Fail(ResultError.Conflict, "This invite has already been used.");
        }
    }

    private static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
}
