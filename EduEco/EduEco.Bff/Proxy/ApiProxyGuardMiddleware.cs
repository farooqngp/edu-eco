using EduEco.Bff.Endpoints;
using EduEco.Bff.Sessions;
using EduEco.Bff.Tokens;
using Microsoft.AspNetCore.Authentication;

namespace EduEco.Bff.Proxy;

/// <summary>
/// Runs inside the YARP pipeline for <c>/api/**</c>: enforces the anti-CSRF header, requires a live server-side session,
/// and obtains (refreshing if needed) the user's access token. Nothing is forwarded when any check fails.
/// </summary>
internal sealed class ApiProxyGuardMiddleware(RequestDelegate next)
{
    public const string AccessTokenItem = "EduEco.Bff.AccessToken";

    public async Task InvokeAsync(HttpContext context, UserTokenService tokenService)
    {
        if (!BffEndpoints.HasCsrfHeader(context))
        {
            await BffEndpoints.CsrfProblem().ExecuteAsync(context);
            return;
        }

        var session = await context.AuthenticateAsync(CookieSchemes.Session);
        if (!session.Succeeded)
        {
            await Results.Problem("No active session.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }

        var accessToken = await tokenService.GetAccessTokenAsync(session.Principal!, context.RequestAborted);
        if (accessToken is null)
        {
            // Session row already removed by the token service; clear the cookie as well.
            await context.SignOutAsync(CookieSchemes.Session);
            await Results.Problem("The session has expired. Sign in again.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }

        context.Items[AccessTokenItem] = accessToken;
        await next(context);
    }
}
