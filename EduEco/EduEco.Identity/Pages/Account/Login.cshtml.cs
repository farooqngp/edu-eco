using System.ComponentModel.DataAnnotations;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ILogger<LoginModel> logger) : PageModel
{
    private const string GenericFailure = "Invalid sign-in attempt.";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; private set; }

    public void OnGet(string? returnUrl) => ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signInManager.PasswordSignInAsync(Input.Email, Input.Password, isPersistent: false, lockoutOnFailure: true);
        var user = await userManager.FindByNameAsync(Input.Email);

        if (result.Succeeded)
        {
            AuditLog.SignInSucceeded(logger, user!.Id, "pwd");
            return LocalRedirect(ReturnUrl);
        }

        if (result.RequiresTwoFactor)
        {
            return RedirectToPage("./LoginWith2fa", new { returnUrl = ReturnUrl });
        }

        if (result.IsLockedOut)
        {
            AuditLog.LockedOut(logger, user?.Id ?? 0);
            return RedirectToPage("./Lockout");
        }

        // Same message for unknown user, wrong password and disabled account (no account enumeration).
        AuditLog.SignInFailed(logger, "pwd", result.IsNotAllowed ? "not-allowed" : "invalid-credentials");
        ModelState.AddModelError(string.Empty, GenericFailure);
        return Page();
    }

    public async Task<IActionResult> OnPostPasskeyOptionsAsync()
    {
        // Discoverable-credential flow: no user hint, so the options do not reveal whether an account exists.
        var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user: null);
        return Content(optionsJson, "application/json");
    }

    public async Task<IActionResult> OnPostPasskeyAsync(string? credentialJson, string? returnUrl)
    {
        ReturnUrl = ReturnUrls.Sanitize(Url, returnUrl);
        if (string.IsNullOrWhiteSpace(credentialJson))
        {
            ModelState.AddModelError(string.Empty, GenericFailure);
            return Page();
        }

        var result = await signInManager.PasskeySignInAsync(credentialJson);
        if (result.Succeeded)
        {
            // The new session cookie is not readable in this request; the subsequent authorization grant audits the user id.
            AuditLog.PasskeySignInSucceeded(logger);
            return LocalRedirect(ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            return RedirectToPage("./Lockout");
        }

        AuditLog.SignInFailed(logger, "passkey", result.IsNotAllowed ? "not-allowed" : "assertion-failed");
        ModelState.AddModelError(string.Empty, GenericFailure);
        return Page();
    }

    public sealed class InputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
