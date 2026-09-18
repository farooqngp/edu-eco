using System.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace EduEco.Identity.Pages.Account;

/// <summary>Confirms an email address from the emailed link. Unconfirmed accounts cannot sign in.</summary>
[AllowAnonymous]
public sealed class ConfirmEmailModel(UserManager<ApplicationUser> userManager, ILogger<ConfirmEmailModel> logger) : PageModel
{
    public bool Confirmed { get; private set; }

    public async Task OnGetAsync(string? userId, string? code)
    {
        var user = string.IsNullOrEmpty(userId) ? null : await userManager.FindByIdAsync(userId);
        if (user is null || string.IsNullOrEmpty(code))
        {
            return;
        }

        try
        {
            var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
            Confirmed = (await userManager.ConfirmEmailAsync(user, token)).Succeeded;
        }
        catch (FormatException)
        {
            Confirmed = false;
        }

        if (Confirmed)
        {
            AuditLog.EmailConfirmed(logger, user.Id);
        }
    }
}
