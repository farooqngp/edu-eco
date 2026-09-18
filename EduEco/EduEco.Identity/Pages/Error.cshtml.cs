using System.Diagnostics;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EduEco.Identity.Pages;

/// <summary>
/// Re-executed for status codes and unhandled exceptions. Shows OpenID Connect protocol errors that cannot be returned
/// to the client (e.g. invalid redirect_uri or missing PKCE detected before the client is trusted). Values are HTML-encoded.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string? Error { get; private set; }

    public string? ErrorDescription { get; private set; }

    public int HttpStatusCode { get; private set; }

    public string RequestId { get; private set; } = string.Empty;

    public void OnGet() => Load();

    public void OnPost() => Load();

    private void Load()
    {
        var response = HttpContext.GetOpenIddictServerResponse();
        Error = response?.Error;
        ErrorDescription = response?.ErrorDescription;
        HttpStatusCode = Response.StatusCode;
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}
