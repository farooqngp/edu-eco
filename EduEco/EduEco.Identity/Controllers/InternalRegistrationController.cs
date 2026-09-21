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

public sealed record InternalRegistrationRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Phone, StringLength(32)] string? PhoneNumber,
    [Required, StringLength(100, MinimumLength = 2)] string DisplayName,
    [Required, StringLength(128, MinimumLength = 12)] string Password);

public enum RegistrationOutcome
{
    Created,
    AlreadyExists,
    ValidationFailed,
}

public sealed record InternalRegistrationResponse(RegistrationOutcome Outcome, long? UserId, IReadOnlyList<string> Errors);

/// <summary>
/// Server-to-server JSON registration for EduEco.Api's Angular-facing <c>/api/v1/auth/register</c>. Mirrors
/// <see cref="Pages.Account.RegisterModel"/> exactly (email confirmation required, no tenant assigned here), except this
/// contract is honest about duplicate emails since only a trusted internal caller sees it — anti-enumeration towards the
/// anonymous browser is Api's responsibility (it must return the same response to the caller regardless of Outcome).
/// </summary>
[ApiController]
[Route("internal/registrations")]
[Authorize(Policy = IdentityServerSetup.IdentityInternalScopePolicy)]
public sealed class InternalRegistrationController(
    UserManager<ApplicationUser> userManager,
    IEmailSender<ApplicationUser> emailSender,
    ILogger<InternalRegistrationController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<InternalRegistrationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InternalRegistrationResponse>> Register(InternalRegistrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Email.Trim();
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return Ok(new InternalRegistrationResponse(RegistrationOutcome.AlreadyExists, null, []));
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim(),
            DisplayName = request.DisplayName.Trim(),
            EmailConfirmed = false,
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.All(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                return Ok(new InternalRegistrationResponse(RegistrationOutcome.AlreadyExists, null, []));
            }

            var errors = result.Errors.Where(e => e.Code is not ("DuplicateUserName" or "DuplicateEmail")).Select(e => e.Description).ToArray();
            return Ok(new InternalRegistrationResponse(RegistrationOutcome.ValidationFailed, null, errors));
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ConfirmEmail", pageHandler: null, values: new { userId = user.Id, code }, protocol: Request.Scheme)!;
        await emailSender.SendConfirmationLinkAsync(user, email, link);
        AuditLog.UserRegistered(logger, user.Id);

        return Ok(new InternalRegistrationResponse(RegistrationOutcome.Created, user.Id, []));
    }
}
