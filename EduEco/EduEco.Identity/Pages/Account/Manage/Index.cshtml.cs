using EduEco.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account.Manage;

public sealed class IndexModel(UserManager<ApplicationUser> userManager) : PageModel
{
    public bool TwoFactorEnabled { get; private set; }

    public int PasskeyCount { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        TwoFactorEnabled = await userManager.GetTwoFactorEnabledAsync(user);
        PasskeyCount = (await userManager.GetPasskeysAsync(user)).Count;
        return Page();
    }
}
