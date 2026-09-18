using System.Text.RegularExpressions;
using EduEco.Bff.Sessions;
using EduEco.Bff.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EduEco.Bff.Endpoints;

public sealed record BffUserResponse(
    string Subject,
    string? Name,
    string? Email,
    long? TenantId,
    DateTimeOffset? SessionExpiresAtUtc,
    string LogoutUrl);

/// <summary>
/// Session management endpoints for the SPA. The SPA never sees tokens: it only learns whether a session exists
/// and who the user is; API calls go through <c>/api/**</c> with the session cookie.
/// </summary>
internal static partial class BffEndpoints
{
    /// <summary>Custom request header required on every data request (forces CORS preflight → blocks cross-site calls).</summary>
    public const string CsrfHeaderName = "X-CSRF";
    public const string CsrfHeaderValue = "1";
    public const string TenantItem = "tenant";
    public const string IdTokenHintItem = "id_token_hint";

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bff").AllowAnonymous().DisableAntiforgery();

        group.MapGet("/login", Login);
        group.MapGet("/logout", LogoutAsync);
        // Two parameters: avoids binding as a RequestDelegate, which would discard the IResult (ASP0016).
        group.MapGet("/user", static (HttpContext context, CancellationToken cancellationToken) => UserAsync(context));

        return endpoints;
    }

    public static bool HasCsrfHeader(HttpContext context) =>
        string.Equals(context.Request.Headers[CsrfHeaderName], CsrfHeaderValue, StringComparison.Ordinal);

    /// <summary>Starts authorization code + PKCE sign-in. Optional <c>tenant</c> selects the tenant at the Identity server.</summary>
    private static Results<ChallengeHttpResult, ProblemHttpResult> Login(string? returnUrl, string? tenant)
    {
        if (!IsLocalPath(returnUrl ?? "/"))
        {
            return TypedResults.Problem("returnUrl must be a local path.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (tenant is not null && !TenantCodeRegex().IsMatch(tenant))
        {
            return TypedResults.Problem("Invalid tenant code.", statusCode: StatusCodes.Status400BadRequest);
        }

        var properties = new AuthenticationProperties { RedirectUri = returnUrl ?? "/" };
        if (tenant is not null)
        {
            properties.Items[TenantItem] = tenant;
        }

        return TypedResults.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// Ends the session: revokes the refresh token, deletes the server-side session, then performs RP-initiated logout at
    /// the Identity server. <c>sid</c> (only obtainable same-origin via /bff/user) prevents cross-site logout.
    /// </summary>
    private static async Task<IResult> LogoutAsync(HttpContext context, string? sid, UserTokenService tokenService, SqlSessionStore sessionStore)
    {
        var session = await context.AuthenticateAsync(CookieSchemes.Session);
        if (!session.Succeeded)
        {
            return TypedResults.Redirect("/");
        }

        var sessionId = SqlSessionStore.GetSessionId(session.Principal!);
        if (sessionId is null || !string.Equals(sid, sessionId, StringComparison.Ordinal))
        {
            return TypedResults.Problem("Invalid or missing session id.", statusCode: StatusCodes.Status400BadRequest);
        }

        var tokens = await sessionStore.GetTokensAsync(sessionId, context.RequestAborted);
        await tokenService.RevokeRefreshTokenAsync(session.Principal!, context.RequestAborted);
        await context.SignOutAsync(CookieSchemes.Session); // deletes the bff.Sessions row

        var properties = new AuthenticationProperties { RedirectUri = "/" };
        if (tokens?.IdentityToken is not null)
        {
            properties.Items[IdTokenHintItem] = tokens.IdentityToken;
        }

        return TypedResults.SignOut(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult> UserAsync(HttpContext context)
    {
        if (!HasCsrfHeader(context))
        {
            return CsrfProblem();
        }

        var session = await context.AuthenticateAsync(CookieSchemes.Session);
        if (!session.Succeeded || SqlSessionStore.GetSessionId(session.Principal!) is not { } sessionId)
        {
            return TypedResults.Problem("No active session.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = session.Principal!;
        return TypedResults.Ok(new BffUserResponse(
            user.FindFirst("sub")?.Value ?? string.Empty,
            user.FindFirst("name")?.Value,
            user.FindFirst("email")?.Value,
            long.TryParse(user.FindFirst(Core.Authorization.EduEcoClaimTypes.TenantId)?.Value, out var tenantId) ? tenantId : null,
            session.Properties?.ExpiresUtc,
            $"/bff/logout?sid={Uri.EscapeDataString(sessionId)}"));
    }

    public static IResult CsrfProblem() =>
        TypedResults.Problem(
            $"The '{CsrfHeaderName}: {CsrfHeaderValue}' request header is required.",
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = "csrf_header_required" });

    /// <summary>Open-redirect guard: "/path" only (rejects "//host", "/\host" and absolute URLs).</summary>
    public static bool IsLocalPath(string url) =>
        url.Length > 0 && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && Uri.TryCreate(url, UriKind.Relative, out _);

    [GeneratedRegex("^[a-z0-9-]{1,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantCodeRegex();
}
