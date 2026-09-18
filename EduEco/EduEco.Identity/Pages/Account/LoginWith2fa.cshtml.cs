using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LoginWith2faModel(SignInManager<ApplicationUser> signInManager, ILogger<LoginWith2faModel> logger) : PageModel
{
    public string ReturnUrl { get; private set; } = "/";

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);
        return await signInManager.GetTwoFactorAuthenticationUserAsync() is null
            ? RedirectToPage("./Login", new { returnUrl = ReturnUrl })
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(string? code, string? returnUrl)
    {
        ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return RedirectToPage("./Login", new { returnUrl = ReturnUrl });
        }

        var normalized = (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(normalized, isPersistent: false, rememberClient: false);

        if (result.Succeeded)
        {
            AuditLog.SignInSucceeded(logger, user.Id, "pwd+totp");
            return LocalRedirect(ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            AuditLog.LockedOut(logger, user.Id);
            return RedirectToPage("./Lockout");
        }

        AuditLog.SignInFailed(logger, "totp", "invalid-code");
        ModelState.AddModelError(string.Empty, "Invalid authenticator code.");
        return Page();
    }
}
