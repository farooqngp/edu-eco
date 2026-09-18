using Microsoft.AspNetCore.Mvc;

namespace EduEco.Identity.Pages.Account;

internal static class ReturnUrls
{
    /// <summary>Open-redirect guard: only local URLs are honoured.</summary>
    public static string Sanitize(IUrlHelper url, string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
