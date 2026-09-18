using EduEco.Identity.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace EduEco.Identity.Pages.Account;

/// <summary>
/// Step-up ("sudo mode") for security-sensitive account pages: the session must have been authenticated within
/// <see cref="IdentityServerOptions.ReauthenticationWindow"/>, otherwise the user is sent to /Account/Reauthenticate.
/// Protects against a hijacked or unattended session silently changing the second factor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireRecentAuthenticationAttribute : Attribute, IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var services = context.HttpContext.RequestServices;
        var window = services.GetRequiredService<IOptions<IdentityServerOptions>>().Value.ReauthenticationWindow;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        var session = await context.HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var issued = session.Properties?.IssuedUtc;

        if (issued is null || now - issued.Value > window)
        {
            var returnUrl = context.HttpContext.Request.Path + context.HttpContext.Request.QueryString;
            context.Result = new RedirectToPageResult("/Account/Reauthenticate", new { returnUrl = returnUrl.ToString() });
            return;
        }

        await next();
    }
}
