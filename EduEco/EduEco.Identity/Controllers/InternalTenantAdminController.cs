using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using EduEco.Identity.Email;
using EduEco.Identity.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace EduEco.Identity.Controllers;

public sealed record InternalTenantAdminRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(100, MinimumLength = 2)] string DisplayName,
    [Required, StringLength(200)] string TenantName,
    [Required, StringLength(2048)] string LoginUrl);

public enum TenantAdminOutcome
{
    Created,
    AlreadyExists,
    ValidationFailed,
}

public sealed record InternalTenantAdminResponse(TenantAdminOutcome Outcome, long? UserId, IReadOnlyList<string> Errors);

/// <summary>
/// Server-to-server provisioning of a tenant-administrator account for EduEco.Api's <c>POST api/v1/tenants</c>.
/// Differs from <see cref="InternalRegistrationController"/> in three ways, all deliberate:
/// <list type="bullet">
/// <item>the account is created <c>EmailConfirmed = true</c>, because an unconfirmed user cannot sign in at all
/// (<c>ActiveUserConfirmation</c>) and cannot be given a tenant membership (<c>IsActiveUserAsync</c>);</item>
/// <item>the password is generated here and disclosed to nobody — the invitee sets their own through the emailed
/// <c>/Account/ResetPassword</c> link, which is what makes "must set a password before first use" true without any
/// must-change-password flag;</item>
/// <item><see cref="InternalTenantAdminResponse.UserId"/> is populated for <see cref="TenantAdminOutcome.AlreadyExists"/>
/// too, because the caller needs it to attach the membership. Only a trusted internal caller sees this contract.</item>
/// </list>
/// </summary>
[ApiController]
[Route("internal/tenant-admins")]
[Authorize(Policy = IdentityServerSetup.IdentityInternalScopePolicy)]
public sealed class InternalTenantAdminController(
    UserManager<ApplicationUser> userManager,
    IProvisioningEmailSender emailSender,
    ILogger<InternalTenantAdminController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<InternalTenantAdminResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InternalTenantAdminResponse>> Provision(InternalTenantAdminRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Email.Trim();
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            // They already have a password; tell them about the new responsibility instead of offering a reset link.
            await emailSender.SendTenantAdminGrantedAsync(existing, email, request.TenantName, request.LoginUrl);
            return Ok(new InternalTenantAdminResponse(TenantAdminOutcome.AlreadyExists, existing.Id, []));
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            EmailConfirmed = true,
            IsActive = true,
        };

        var result = await userManager.CreateAsync(user, GeneratePassword());
        if (!result.Succeeded)
        {
            if (result.Errors.All(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                var raced = await userManager.FindByEmailAsync(email);
                return Ok(new InternalTenantAdminResponse(TenantAdminOutcome.AlreadyExists, raced?.Id, []));
            }

            var errors = result.Errors.Where(e => e.Code is not ("DuplicateUserName" or "DuplicateEmail")).Select(e => e.Description).ToArray();
            return Ok(new InternalTenantAdminResponse(TenantAdminOutcome.ValidationFailed, null, errors));
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ResetPassword", pageHandler: null, values: new { userId = user.Id, code }, protocol: Request.Scheme)!;
        await emailSender.SendTenantAdminInvitationAsync(user, email, request.TenantName, link, request.LoginUrl);
        AuditLog.UserRegistered(logger, user.Id);

        return Ok(new InternalTenantAdminResponse(TenantAdminOutcome.Created, user.Id, []));
    }

    /// <summary>
    /// A throwaway password satisfying the configured policy (length 12 plus the ASP.NET Core defaults: digit,
    /// lowercase, uppercase, non-alphanumeric). It is never returned, logged or emailed — the invitee replaces it
    /// through the reset link, so nobody, including the provisioning admin, ever knows this value.
    /// </summary>
    private static string GeneratePassword()
    {
        const string Lower = "abcdefghijkmnopqrstuvwxyz";
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string Digits = "23456789";
        const string Symbols = "!@#$%^&*-_=+";

        char[] password =
        [
            .. RandomNumberGenerator.GetString(Lower + Upper + Digits + Symbols, 20),
            RandomNumberGenerator.GetString(Lower, 1)[0],
            RandomNumberGenerator.GetString(Upper, 1)[0],
            RandomNumberGenerator.GetString(Digits, 1)[0],
            RandomNumberGenerator.GetString(Symbols, 1)[0],
        ];

        RandomNumberGenerator.Shuffle(password.AsSpan());
        return new string(password);
    }
}
