namespace EduEco.Application.Invites;

public sealed record InviteLookup(long Id, long TenantId, string RoleName, DateTimeOffset ExpiresAtUtc, int MaxUses, int UseCount);

public interface IInviteQueries
{
    /// <summary>
    /// Cross-tenant lookup by code hash: unfiltered by design, since redemption happens before the caller has any
    /// tenant/token context (self-registration is anonymous).
    /// </summary>
    Task<InviteLookup?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken = default);
}
