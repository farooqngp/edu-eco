using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account;

/// <summary>Re-authentication for step-up. A fresh sign-in resets the session's authentication time.</summary>
public sealed class ReauthenticateModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ILogger<ReauthenticateModel> logger) : PageModel
{
    public string ReturnUrl { get; private set; } = "/Account/Manage";

    public IActionResult OnGet(string? returnUrl)
    {
        ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? password, string? returnUrl)
    {
        ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var check = await signInManager.CheckPasswordSignInAsync(user, password ?? string.Empty, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            await signInManager.SignOutAsync();
            return RedirectToPage("./Lockout");
        }

        if (!check.Succeeded)
        {
            AuditLog.SignInFailed(logger, "reauth", "invalid-credentials");
            ModelState.AddModelError(string.Empty, "The password is incorrect.");
            return Page();
        }

        // New session cookie → IssuedUtc = now (the step-up clock).
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: "pwd");
        AuditLog.Reauthenticated(logger, user.Id);
        return LocalRedirect(ReturnUrl);
    }
}
