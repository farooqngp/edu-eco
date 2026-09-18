using System.Text;
using System.Text.Encodings.Web;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account.Manage;

[RequireRecentAuthentication]
public sealed class TwoFactorModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    UrlEncoder urlEncoder,
    ILogger<TwoFactorModel> logger) : PageModel
{
    private const string Issuer = "EduEco";
    private const int RecoveryCodeCount = 10;

    public bool IsEnabled { get; private set; }

    public string? SharedKey { get; private set; }

    public string? AuthenticatorUri { get; private set; }

    public int RecoveryCodesLeft { get; private set; }

    public string[]? RecoveryCodes { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(string? code)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var normalized = (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var valid = normalized.Length > 0 && await userManager.VerifyTwoFactorTokenAsync(
            user, userManager.Options.Tokens.AuthenticatorTokenProvider, normalized);

        if (!valid)
        {
            ModelState.AddModelError(string.Empty, "The verification code is invalid.");
            await LoadAsync(user);
            return Page();
        }

        // Updates the security stamp: outstanding refresh tokens for this user are invalidated.
        await userManager.SetTwoFactorEnabledAsync(user, true);
        RecoveryCodes = [.. await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) ?? []];
        await signInManager.RefreshSignInAsync(user);
        AuditLog.TwoFactorEnabled(logger, user.Id);

        IsEnabled = true;
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);
        await signInManager.RefreshSignInAsync(user);
        AuditLog.TwoFactorDisabled(logger, user.Id);

        StatusMessage = "Two-factor authentication has been disabled.";
        return RedirectToPage();
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        IsEnabled = await userManager.GetTwoFactorEnabledAsync(user);
        if (IsEnabled)
        {
            RecoveryCodesLeft = await userManager.CountRecoveryCodesAsync(user);
            return;
        }

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        SharedKey = FormatKey(key!);
        var account = await userManager.GetEmailAsync(user) ?? user.UserName ?? user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AuthenticatorUri = $"otpauth://totp/{urlEncoder.Encode(Issuer)}:{urlEncoder.Encode(account)}?secret={key}&issuer={urlEncoder.Encode(Issuer)}&digits=6";
    }

    private static string FormatKey(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            builder.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return builder.ToString().TrimEnd().ToLowerInvariant();
    }
}
