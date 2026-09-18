using EduEco.Application.Tenants;
using EduEco.Core.Identity;
using EduEco.Identity.Controllers;
using EduEco.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace EduEco.Identity.Pages.Account;

/// <summary>Tenant picker shown during authorization when the user can act for more than one tenant.</summary>
public sealed class SelectTenantModel(UserManager<ApplicationUser> userManager, TenantAccessResolver tenantAccessResolver) : PageModel
{
    private const string AuthorizePath = "/connect/authorize";

    public IReadOnlyList<TenantMembershipSummary> Tenants { get; private set; } = [];

    public bool IsPlatformAdmin { get; private set; }

    public string ReturnUrl { get; private set; } = AuthorizePath;

    public string? Search { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? search, CancellationToken cancellationToken)
    {
        if (!TryAcceptReturnUrl(returnUrl))
        {
            return BadRequest();
        }

        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        Search = search;
        IsPlatformAdmin = await tenantAccessResolver.IsPlatformAdminAsync(user);
        Tenants = await tenantAccessResolver.GetSelectableTenantsAsync(user, search, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl, string? tenantCode, CancellationToken cancellationToken)
    {
        if (!TryAcceptReturnUrl(returnUrl) || string.IsNullOrWhiteSpace(tenantCode))
        {
            return BadRequest();
        }

        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        // Pre-check for a friendly error; the authorize endpoint re-validates access authoritatively.
        var selection = await tenantAccessResolver.SelectAsync(user, tenantCode, cancellationToken);
        if (selection.Kind != TenantSelectionKind.Selected)
        {
            ModelState.AddModelError(string.Empty, "You do not have access to that tenant.");
            return await OnGetAsync(returnUrl, null, cancellationToken);
        }

        var uri = new Uri("https://placeholder" + ReturnUrl);
        var query = QueryHelpers.ParseQuery(uri.Query);
        query[AuthorizationController.TenantParameter] = new StringValues(selection.Tenant!.Code);

        return LocalRedirect(AuthorizePath + QueryString.Create(query));
    }

    /// <summary>Only authorization request URLs are accepted (prevents use as an open redirect).</summary>
    private bool TryAcceptReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl)
            || !returnUrl.StartsWith(AuthorizePath + "?", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReturnUrl = returnUrl;
        return true;
    }
}
