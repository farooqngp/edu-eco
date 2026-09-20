using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Common;
using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using EduEco.Core.Tenants;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Persistence.Dapper;
using Microsoft.Extensions.Options;

namespace EduEco.Api.Tenants;

public sealed record ProvisionTenantCommand(string Code, string Name, string AdminEmail, string AdminDisplayName);

public sealed record TenantProvisioned(long TenantId, string Code, string Name, long AdminUserId, bool InvitationSent);

/// <summary>
/// Creates a tenant together with its administrator: the account is provisioned through Identity (which emails a
/// set-password link), then the tenant row and the TenantAdmin membership are written in one local transaction.
/// <para>
/// Lives in the Api rather than Application because the membership must be written against the *new* tenant, not the
/// caller's ambient one — which needs <see cref="DapperCommandRepository{T}"/> and a fixed tenant context, exactly as
/// <see cref="Controllers.AuthController"/> already does when an invite is redeemed anonymously.
/// </para>
/// </summary>
public sealed partial class TenantProvisioningService(
    ICommandRepository<Tenant> tenants,
    ITenantQueries tenantQueries,
    ITenantAdminProvisioner adminProvisioner,
    IPermissionCacheInvalidator permissionCache,
    IDbSession dbSession,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<TenantProvisioningService> logger)
{
    public async Task<Result<TenantProvisioned>> ProvisionAsync(ProvisionTenantCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var code = command.Code.Trim();
        var name = command.Name.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return Result<TenantProvisioned>.Fail(ResultError.Validation, "Code and Name are required.");
        }

        // Check the code before provisioning the account: a duplicate code is the likeliest failure by far, and the
        // remote user creation cannot be rolled back. The unique index below remains the race-condition backstop.
        if (await tenantQueries.FindActiveTenantByCodeAsync(code, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result<TenantProvisioned>.Fail(ResultError.Conflict, $"A tenant with code '{code}' already exists.");
        }

        var admin = await adminProvisioner.ProvisionAsync(command.AdminEmail.Trim(), command.AdminDisplayName.Trim(), name, cancellationToken).ConfigureAwait(false);
        if (admin.Outcome == AdminProvisionOutcome.ValidationFailed || admin.UserId is null)
        {
            var detail = admin.Errors.Count > 0 ? string.Join(' ', admin.Errors) : "The administrator account could not be created.";
            return Result<TenantProvisioned>.Fail(ResultError.Validation, detail);
        }

        var adminUserId = admin.UserId.Value;
        var tenant = new Tenant { Code = code, Name = name };

        var unitOfWork = (IUnitOfWork)dbSession;
        await unitOfWork.BeginAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await tenants.InsertAsync(tenant, cancellationToken).ConfigureAwait(false);

            var memberships = new DapperCommandRepository<UserTenantMembership>(
                dbSession, new FixedTenantContext(tenant.Id), currentUser, timeProvider, databaseOptions);

            try
            {
                await memberships.InsertAsync(
                    new UserTenantMembership
                    {
                        UserId = adminUserId,
                        RoleId = Roles.All.Single(r => r.Name == Roles.TenantAdmin).Id,
                        IsDefault = true,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DuplicateEntityException)
            {
                // Only reachable when the same provisioning request is replayed: treat as success so a retry after a
                // partial failure converges instead of dead-ending on the unique constraint.
            }

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateEntityException)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            LogAdminWithoutTenant(logger, adminUserId, code);
            return Result<TenantProvisioned>.Fail(ResultError.Conflict, $"A tenant with code '{code}' already exists.");
        }
        catch
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            LogAdminWithoutTenant(logger, adminUserId, code);
            throw;
        }

        await permissionCache.InvalidateUserAsync(adminUserId, cancellationToken).ConfigureAwait(false);

        return Result<TenantProvisioned>.Ok(new TenantProvisioned(
            tenant.Id, tenant.Code, tenant.Name, adminUserId, InvitationSent: admin.Outcome == AdminProvisionOutcome.Created));
    }

    /// <summary>The account creation in Identity cannot be rolled back, so make the leftover discoverable.</summary>
    [LoggerMessage(EventId = 6100, Level = LogLevel.Warning,
        Message = "AUDIT tenant provisioning failed after the admin account was created: user {AdminUserId} has no membership, attempted tenant code {Code}. Re-POST the same request to repair.")]
    private static partial void LogAdminWithoutTenant(ILogger logger, long adminUserId, string code);

    private sealed class FixedTenantContext(long tenantId) : ITenantContext
    {
        public long? TenantId { get; } = tenantId;

        public bool IsCrossTenant => false;
    }
}
