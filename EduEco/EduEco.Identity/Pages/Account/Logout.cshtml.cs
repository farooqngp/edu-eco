using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LogoutModel : PageModel
{
    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; private set; } = [];

    public void OnGet() =>
        Parameters = [.. Request.Query
            .Where(p => !string.Equals(p.Key, "__RequestVerificationToken", StringComparison.Ordinal))
            .SelectMany(p => p.Value.Select(v => KeyValuePair.Create(p.Key, v ?? string.Empty)))];
}
