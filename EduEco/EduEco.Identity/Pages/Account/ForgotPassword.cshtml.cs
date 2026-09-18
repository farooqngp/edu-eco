using System.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace EduEco.Identity.Pages.Account;

/// <summary>Password reset request. Same response whether or not the account exists (no enumeration).</summary>
[AllowAnonymous]
public sealed class ForgotPasswordModel(
    UserManager<ApplicationUser> userManager,
    IEmailSender<ApplicationUser> emailSender,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    public bool Submitted { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? email)
    {
        Submitted = true;
        AuditLog.PasswordResetRequested(logger);

        if (string.IsNullOrWhiteSpace(email))
        {
            return Page();
        }

        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive || !await userManager.IsEmailConfirmedAsync(user))
        {
            return Page();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ResetPassword", pageHandler: null, values: new { userId = user.Id, code }, protocol: Request.Scheme)!;

        await emailSender.SendPasswordResetLinkAsync(user, user.Email!, link);
        return Page();
    }
}
