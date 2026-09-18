using System.Text;
using EduEco.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace EduEco.Identity.Pages.Account;

/// <summary>Sends a new confirmation link. Same response for every input (no enumeration).</summary>
[AllowAnonymous]
public sealed class ResendEmailConfirmationModel(
    UserManager<ApplicationUser> userManager,
    IEmailSender<ApplicationUser> emailSender) : PageModel
{
    public bool Submitted { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? email)
    {
        Submitted = true;
        var user = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive || await userManager.IsEmailConfirmedAsync(user))
        {
            return Page();
        }

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = Url.Page("/Account/ConfirmEmail", pageHandler: null, values: new { userId = user.Id, code }, protocol: Request.Scheme)!;
        await emailSender.SendConfirmationLinkAsync(user, user.Email!, link);
        return Page();
    }
}
