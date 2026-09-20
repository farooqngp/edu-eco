namespace EduEco.Application.Invites;

/// <summary>
/// Delivers an issued invite code to the invitee. Best-effort by contract: implementations catch and log their own
/// delivery failures instead of throwing, because a failed email must never fail invite issuance (the code is already
/// persisted and is still returned to the caller).
/// </summary>
public interface IInviteEmailSender
{
    Task SendInviteAsync(string email, string code, string roleName, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default);
}
