using System.Globalization;
using System.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using EduEco.Identity.Logout;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace EduEco.Identity.Pages.Account;

/// <summary>
/// Completes a password reset. Success rotates the security stamp (refresh tokens become unusable), clears lockout,
/// and triggers global logout so every existing session and relying-party session ends.
/// </summary>
[AllowAnonymous]
public sealed class ResetPasswordModel(
    UserManager<ApplicationUser> userManager,
    BackchannelLogoutNotifier logoutNotifier,
    ILogger<ResetPasswordModel> logger) : PageModel
{
    private const string InvalidLink = "The reset link is invalid or has expired. Request a new one.";

    public string? UserId { get; private set; }

    public string? Code { get; private set; }

    public bool Completed { get; private set; }

    public IActionResult OnGet(string? userId, string? code)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
        {
            return RedirectToPage("./ForgotPassword");
        }

        UserId = userId;
        Code = code;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? userId, string? code, string? password, string? confirmPassword)
    {
        UserId = userId;
        Code = code;

        if (string.IsNullOrEmpty(password) || !string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(string.Empty, "The passwords do not match.");
            return Page();
        }

        var user = string.IsNullOrEmpty(userId) ? null : await userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive || string.IsNullOrEmpty(code))
        {
            ModelState.AddModelError(string.Empty, InvalidLink);
            return Page();
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            ModelState.AddModelError(string.Empty, InvalidLink);
            return Page();
        }

        var result = await userManager.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Code == "InvalidToken" ? InvalidLink : error.Description);
            }

            return Page();
        }

        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        logoutNotifier.Enqueue(user.Id.ToString(CultureInfo.InvariantCulture));
        AuditLog.PasswordResetCompleted(logger, user.Id);

        Completed = true;
        return Page();
    }
}
