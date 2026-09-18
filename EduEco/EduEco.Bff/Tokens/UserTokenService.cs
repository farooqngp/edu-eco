using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using EduEco.Bff.Configuration;
using EduEco.Bff.Sessions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace EduEco.Bff.Tokens;

/// <summary>
/// Supplies a valid access token for the session, refreshing it (rotating refresh token) shortly before expiry.
/// Refresh is serialised per session: Identity enforces one-time refresh tokens and revokes the whole chain on reuse,
/// so concurrent SPA requests must never redeem the same refresh token twice. An in-process gate collapses local
/// concurrency; a SQL Server application lock serialises refreshes across BFF instances.
/// </summary>
public sealed partial class UserTokenService(
    SqlSessionStore sessionStore,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<BffOptions> options,
    BffClientAuthentication clientAuthentication,
    SqlRefreshLock refreshLock,
    TimeProvider timeProvider,
    ILogger<UserTokenService> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new(StringComparer.Ordinal);

    public async Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var sessionId = SqlSessionStore.GetSessionId(user);
        if (sessionId is null)
        {
            return null;
        }

        var tokens = await sessionStore.GetTokensAsync(sessionId, cancellationToken);
        if (tokens is null)
        {
            return null;
        }

        if (IsFresh(tokens))
        {
            return tokens.AccessToken;
        }

        var gate = RefreshLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var distributedLock = await refreshLock.AcquireAsync(sessionId, SqlRefreshLock.DefaultTimeout, cancellationToken);
            if (distributedLock is null)
            {
                // Another instance is still refreshing: fall back to the current token while it has not expired yet.
                LogRefreshLockTimeout(logger, sessionId);
                return tokens.AccessTokenExpiresAtUtc > timeProvider.GetUtcNow() ? tokens.AccessToken : null;
            }

            tokens = await sessionStore.GetTokensAsync(sessionId, cancellationToken); // another request may have refreshed
            if (tokens is null)
            {
                return null;
            }

            if (IsFresh(tokens))
            {
                return tokens.AccessToken;
            }

            var refreshed = tokens.RefreshToken is null ? null : await RefreshAsync(tokens, cancellationToken);
            if (refreshed is null)
            {
                // Refresh token expired, revoked or reused: the session cannot call the API any more.
                LogRefreshFailed(logger, sessionId);
                await sessionStore.RemoveBySessionIdAsync(sessionId, cancellationToken);
                return null;
            }

            await sessionStore.UpdateTokensAsync(sessionId, refreshed, cancellationToken);
            LogRefreshed(logger, sessionId);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Best-effort RFC 7009 revocation of the refresh token (and its chain) at sign-out.</summary>
    public async Task RevokeRefreshTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var sessionId = SqlSessionStore.GetSessionId(user);
        var tokens = sessionId is null ? null : await sessionStore.GetTokensAsync(sessionId, cancellationToken);
        if (tokens?.RefreshToken is null)
        {
            return;
        }

        var oidc = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        var revocationEndpoint = !string.IsNullOrEmpty(configuration.RevocationEndpoint)
            ? configuration.RevocationEndpoint
            : configuration.AdditionalData.TryGetValue("revocation_endpoint", out var endpoint) ? endpoint as string : null;
        if (!string.IsNullOrEmpty(revocationEndpoint))
        {
            var form = new Dictionary<string, string>
            {
                ["token"] = tokens.RefreshToken,
                ["token_type_hint"] = "refresh_token",
            };
            clientAuthentication.Apply(form, configuration.Issuer);

            using var response = await oidc.Backchannel.PostAsync(revocationEndpoint, new FormUrlEncodedContent(form), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                LogRevocationFailed(logger, (int)response.StatusCode);
            }
        }

        RefreshLocks.TryRemove(sessionId!, out _);
    }

    private bool IsFresh(SessionTokens tokens) =>
        tokens.AccessTokenExpiresAtUtc - timeProvider.GetUtcNow() > options.Value.AccessTokenRefreshThreshold;

    private async Task<SessionTokens?> RefreshAsync(SessionTokens current, CancellationToken cancellationToken)
    {
        var oidc = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken!,
        };
        clientAuthentication.Apply(form, configuration.Issuer);

        using var response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = json.RootElement;
        if (!root.TryGetProperty("access_token", out var accessToken))
        {
            return null;
        }

        var expiresIn = root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 300;
        return new SessionTokens(
            accessToken.GetString()!,
            root.TryGetProperty("refresh_token", out var refreshToken) ? refreshToken.GetString() : current.RefreshToken,
            root.TryGetProperty("id_token", out var idToken) ? idToken.GetString() : current.IdentityToken,
            timeProvider.GetUtcNow().AddSeconds(expiresIn));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Access token refreshed for session {SessionId}")]
    private static partial void LogRefreshed(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AUDIT token refresh failed; session {SessionId} ended")]
    private static partial void LogRefreshFailed(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timed out waiting for the refresh lock of session {SessionId}")]
    private static partial void LogRefreshLockTimeout(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh token revocation returned {StatusCode}")]
    private static partial void LogRevocationFailed(ILogger logger, int statusCode);
}
