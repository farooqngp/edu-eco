using System.ComponentModel.DataAnnotations;
using System.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using EduEco.Identity.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace EduEco.Identity.Controllers;

public sealed record InternalPasswordResetRequest([Required, EmailAddress, StringLength(256)] string Email);

/// <summary>
/// Server-to-server JSON password-reset request for EduEco.Api's <c>/api/v1/auth/forgot-password</c>. Mirrors
/// <see cref="Pages.Account.ForgotPasswordModel"/>'s always-succeed, no-enumeration behavior — this endpoint has no
/// meaningful response to differentiate anyway (Api relays a flat "check your email" regardless of what happens here).
/// </summary>
[ApiController]
[Route("internal/password-resets")]
[Authorize(Policy = IdentityServerSetup.IdentityInternalScopePolicy)]
public sealed class InternalPasswordResetController(
    UserManager<ApplicationUser> userManager,
    IEmailSender<ApplicationUser> emailSender,
    ILogger<InternalPasswordResetController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RequestReset(InternalPasswordResetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AuditLog.PasswordResetRequested(logger);

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive || !await userManager.IsEmailConfirmedAsync(user))
        {
            return Accepted();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ResetPassword", pageHandler: null, values: new { userId = user.Id, code }, protocol: Request.Scheme)!;
        await emailSender.SendPasswordResetLinkAsync(user, user.Email!, link);

        return Accepted();
    }
}
