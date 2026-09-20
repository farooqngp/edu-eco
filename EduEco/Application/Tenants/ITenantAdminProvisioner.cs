namespace EduEco.Application.Tenants;

public enum AdminProvisionOutcome
{
    Created,
    AlreadyExists,
    ValidationFailed,
}

/// <summary><see cref="AdminProvisionResult.UserId"/> is set for <see cref="AdminProvisionOutcome.AlreadyExists"/> too — the caller needs it to attach the membership.</summary>
public sealed record AdminProvisionResult(AdminProvisionOutcome Outcome, long? UserId, IReadOnlyList<string> Errors);

/// <summary>
/// Creates (or finds) the user account that will administer a tenant, and emails them a set-password link. Identity
/// owns the account store and the reset token, so this is a port: the resource server implements it as a
/// server-to-server call rather than touching Identity's tables.
/// </summary>
public interface ITenantAdminProvisioner
{
    Task<AdminProvisionResult> ProvisionAsync(string email, string displayName, string tenantName, CancellationToken cancellationToken = default);
}
