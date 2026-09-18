using System.Buffers.Text;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account.Manage;

[RequireRecentAuthentication]
public sealed class PasskeysModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ILogger<PasskeysModel> logger) : PageModel
{
    private const int MaxPasskeysPerUser = 10;

    public sealed record PasskeyItem(string Id, string Name, DateTimeOffset CreatedAt);

    public IReadOnlyList<PasskeyItem> Passkeys { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        Passkeys = [.. (await userManager.GetPasskeysAsync(user))
            .Select(p => new PasskeyItem(Base64Url.EncodeToString(p.CredentialId), p.Name ?? "Passkey", p.CreatedAt))];
        return Page();
    }

    public async Task<IActionResult> OnPostCreationOptionsAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var userName = await userManager.GetUserNameAsync(user) ?? string.Empty;
        var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = await userManager.GetUserIdAsync(user),
            Name = userName,
            DisplayName = user.DisplayName ?? userName,
        });

        return Content(optionsJson, "application/json");
    }

    public async Task<IActionResult> OnPostRegisterAsync(string? credentialJson, string? name)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        if (string.IsNullOrWhiteSpace(credentialJson))
        {
            StatusMessage = "Passkey registration failed.";
            return RedirectToPage();
        }

        if ((await userManager.GetPasskeysAsync(user)).Count >= MaxPasskeysPerUser)
        {
            StatusMessage = $"You can register at most {MaxPasskeysPerUser} passkeys.";
            return RedirectToPage();
        }

        var attestation = await signInManager.PerformPasskeyAttestationAsync(credentialJson);
        if (!attestation.Succeeded)
        {
            StatusMessage = "Passkey registration failed.";
            return RedirectToPage();
        }

        var passkey = attestation.Passkey;
        passkey.Name = string.IsNullOrWhiteSpace(name) ? "Passkey" : name.Trim()[..Math.Min(name.Trim().Length, 100)];

        var result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
        if (!result.Succeeded)
        {
            StatusMessage = "Passkey registration failed.";
            return RedirectToPage();
        }

        AuditLog.PasskeyRegistered(logger, user.Id);
        StatusMessage = "Passkey added.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string? id)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(id ?? string.Empty);
        }
        catch (FormatException)
        {
            return BadRequest();
        }

        var result = await userManager.RemovePasskeyAsync(user, credentialId);
        if (result.Succeeded)
        {
            AuditLog.PasskeyRemoved(logger, user.Id);
            StatusMessage = "Passkey removed.";
        }

        return RedirectToPage();
    }
}
