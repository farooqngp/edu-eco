using EduEco.Core.Identity;

namespace EduEco.Identity.Email;

/// <summary>
/// Emails for accounts an administrator provisions on someone else's behalf. Separate from ASP.NET Identity's
/// <c>IEmailSender&lt;ApplicationUser&gt;</c>, whose three fixed methods (confirmation link, reset link, reset code)
/// cannot express "welcome, set your password". Implemented by <see cref="SmtpEmailSender"/>, which already owns the
/// MailKit plumbing and <see cref="SmtpOptions"/>.
/// </summary>
public interface IProvisioningEmailSender
{
    /// <summary>
    /// Invites a newly provisioned tenant administrator to set their own password. The account is created with a
    /// random password nobody knows, so this link is the only way in — hence it must appear before
    /// <paramref name="loginUrl"/> in the body (the E2E Mailpit helper reads the first link).
    /// </summary>
    Task SendTenantAdminInvitationAsync(ApplicationUser user, string email, string tenantName, string setPasswordLink, string loginUrl);

    /// <summary>Notifies an existing account that it has been granted administration of another tenant.</summary>
    Task SendTenantAdminGrantedAsync(ApplicationUser user, string email, string tenantName, string loginUrl);
}
