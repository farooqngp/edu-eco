using System.Text.Json;
using EduEco.Bff.Configuration;
using EduEco.Bff.Sessions;
using EduEco.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Bff.Endpoints;

/// <summary>
/// OpenID Connect Back-Channel Logout 1.0 receiver. The Identity server POSTs a signed <c>logout+jwt</c> when the user
/// signs out elsewhere, resets their password or is disabled; every BFF session of that subject is deleted, so the
/// browser cookie stops working immediately.
/// </summary>
internal static partial class BackchannelLogoutEndpoint
{
    public const string Path = "/bff/backchannel-logout";
    public const string LogoutTokenType = "logout+jwt";
    public const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly JsonWebTokenHandler TokenHandler = new() { MapInboundClaims = false };

    public static IEndpointRouteBuilder MapBackchannelLogout(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Path, HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .Accepts<IFormCollection>("application/x-www-form-urlencoded");
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
        IOptions<BffOptions> options,
        IReplayCache replayCache,
        SqlSessionStore sessionStore,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(BackchannelLogoutEndpoint));
        context.Response.Headers.CacheControl = "no-store";

        if (!context.Request.HasFormContentType)
        {
            return Error("The request must be application/x-www-form-urlencoded.");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["logout_token"] is not [{ Length: > 0 } logoutToken])
        {
            return Error("logout_token is required.");
        }

        var oidc = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var result = await ValidateAsync(oidc, options.Value.ClientId, logoutToken, context.RequestAborted);
        if (!result.IsValid)
        {
            // Signing key rollover: refresh the discovery document once and retry.
            oidc.ConfigurationManager!.RequestRefresh();
            result = await ValidateAsync(oidc, options.Value.ClientId, logoutToken, context.RequestAborted);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            LogRejected(logger, result.Exception?.Message ?? "invalid token");
            return Error("The logout token is invalid.");
        }

        if (!HasLogoutEvent(token) || token.TryGetPayloadValue<object>("nonce", out _))
        {
            LogRejected(logger, "missing back-channel logout event or nonce present");
            return Error("The logout token is invalid.");
        }

        if (!token.TryGetPayloadValue<string>("sub", out var subject) || string.IsNullOrEmpty(subject))
        {
            // Session-only (sid) logout tokens are not issued by EduEco.Identity.
            LogRejected(logger, "sub claim missing");
            return Error("The logout token must contain a sub claim.");
        }

        if (string.IsNullOrEmpty(token.Id))
        {
            LogRejected(logger, "jti claim missing");
            return Error("The logout token must contain a jti claim.");
        }

        // Remember the jti until the token could no longer pass lifetime validation anyway.
        var lifetime = token.ValidTo - DateTime.UtcNow + ClockSkew;
        if (!await replayCache.TryAddAsync($"logout:{token.Issuer}:{token.Id}", lifetime > TimeSpan.Zero ? lifetime : ClockSkew, context.RequestAborted))
        {
            LogRejected(logger, "replayed logout token");
            return Error("The logout token was already used.");
        }

        var removed = await sessionStore.RemoveBySubjectAsync(subject, context.RequestAborted);
        LogLoggedOut(logger, subject, removed);
        return TypedResults.Ok();
    }

    private static async Task<TokenValidationResult> ValidateAsync(
        OpenIdConnectOptions oidc, string clientId, string logoutToken, CancellationToken cancellationToken)
    {
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        return await TokenHandler.ValidateTokenAsync(logoutToken, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidAudience = clientId,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidTypes = [LogoutTokenType],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
        });
    }

    private static bool HasLogoutEvent(JsonWebToken token)
    {
        if (!token.TryGetClaim("events", out var claim) || string.IsNullOrEmpty(claim.Value))
        {
            return false;
        }

        try
        {
            using var events = JsonDocument.Parse(claim.Value);
            return events.RootElement.ValueKind == JsonValueKind.Object
                && events.RootElement.TryGetProperty(LogoutEvent, out var value)
                && value.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IResult Error(string description) =>
        TypedResults.BadRequest(new Dictionary<string, string>
        {
            ["error"] = "invalid_request",
            ["error_description"] = description,
        });

    [LoggerMessage(EventId = 6010, Level = LogLevel.Information, Message = "AUDIT back-channel logout for {Subject}: {Count} session(s) ended")]
    private static partial void LogLoggedOut(ILogger logger, string subject, int count);

    [LoggerMessage(EventId = 6011, Level = LogLevel.Warning, Message = "AUDIT back-channel logout rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
