using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using EduEco.Bff.Configuration;
using EduEco.Infrastructure.Persistence.Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace EduEco.Bff.Sessions;

public sealed record SessionTokens(string AccessToken, string? RefreshToken, string? IdentityToken, DateTimeOffset AccessTokenExpiresAtUtc);

/// <summary>
/// Server-side cookie session store (bff.Sessions). The browser receives only an opaque 256-bit key; the database keeps
/// SHA-256(key), the encrypted ticket (claims) and the encrypted OAuth tokens. Tokens never reach the browser or the ticket.
/// Deleting the row ends the session immediately, even if the cookie is replayed.
/// </summary>
public sealed class SqlSessionStore(
    IDbConnectionFactory connectionFactory,
    IDataProtectionProvider dataProtection,
    TimeProvider timeProvider,
    IOptions<BffOptions> options) : ITicketStore
{
    /// <summary>Public per-session id claim (logout CSRF token, token lookups). Not a credential.</summary>
    public const string SessionIdClaim = "bff_sid";

    private readonly IDataProtector _ticketProtector = dataProtection.CreateProtector("EduEco.Bff.Sessions.Ticket.v1");
    private readonly IDataProtector _tokenProtector = dataProtection.CreateProtector("EduEco.Bff.Sessions.Tokens.v1");

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var sessionId = ticket.Principal.FindFirst(SessionIdClaim)?.Value
            ?? throw new InvalidOperationException($"Ticket has no '{SessionIdClaim}' claim.");
        var tokens = ExtractTokens(ticket.Properties);
        var now = timeProvider.GetUtcNow();
        var key = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        await using var connection = connectionFactory.CreateWriteConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO bff.Sessions
                (SessionKeyHash, SessionId, Subject, TenantId, Ticket, Tokens, AccessTokenExpiresAtUtc, CreatedAtUtc, RenewedAtUtc, ExpiresAtUtc)
            VALUES
                (@SessionKeyHash, @SessionId, @Subject, @TenantId, @Ticket, @Tokens, @AccessTokenExpiresAtUtc, @Now, @Now, @ExpiresAtUtc);
            """,
            new
            {
                SessionKeyHash = Hash(key),
                SessionId = sessionId,
                Subject = ticket.Principal.FindFirst("sub")?.Value ?? string.Empty,
                TenantId = long.TryParse(ticket.Principal.FindFirst(Core.Authorization.EduEcoClaimTypes.TenantId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId) ? tenantId : (long?)null,
                Ticket = ProtectTicket(ticket),
                Tokens = tokens is null ? null : ProtectTokens(tokens),
                AccessTokenExpiresAtUtc = tokens?.AccessTokenExpiresAtUtc,
                Now = now,
                ExpiresAtUtc = ticket.Properties.ExpiresUtc ?? now + options.Value.SessionLifetime,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var tokens = ExtractTokens(ticket.Properties);
        var now = timeProvider.GetUtcNow();

        await using var connection = connectionFactory.CreateWriteConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE bff.Sessions
            SET Ticket = @Ticket,
                RenewedAtUtc = @Now,
                ExpiresAtUtc = @ExpiresAtUtc,
                Tokens = COALESCE(@Tokens, Tokens),
                AccessTokenExpiresAtUtc = COALESCE(@AccessTokenExpiresAtUtc, AccessTokenExpiresAtUtc)
            WHERE SessionKeyHash = @SessionKeyHash;
            """,
            new
            {
                SessionKeyHash = Hash(key),
                Ticket = ProtectTicket(ticket),
                Tokens = tokens is null ? null : ProtectTokens(tokens),
                AccessTokenExpiresAtUtc = tokens?.AccessTokenExpiresAtUtc,
                Now = now,
                ExpiresAtUtc = ticket.Properties.ExpiresUtc ?? now + options.Value.SessionLifetime,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateReadConnection();
        var protectedTicket = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT Ticket FROM bff.Sessions WHERE SessionKeyHash = @SessionKeyHash AND ExpiresAtUtc > @Now;",
            new { SessionKeyHash = Hash(key), Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (protectedTicket is null)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_ticketProtector.Unprotect(protectedTicket));
        }
        catch (CryptographicException)
        {
            return null; // Key ring rotated/lost: treat as signed out.
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateWriteConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bff.Sessions WHERE SessionKeyHash = @SessionKeyHash;",
            new { SessionKeyHash = Hash(key) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SessionTokens?> GetTokensAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateWriteConnection(); // primary: avoid replica lag during rotation
        var protectedTokens = await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
            "SELECT Tokens FROM bff.Sessions WHERE SessionId = @SessionId AND ExpiresAtUtc > @Now;",
            new { SessionId = sessionId, Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (protectedTokens is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionTokens>(_tokenProtector.Unprotect(protectedTokens));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task UpdateTokensAsync(string sessionId, SessionTokens tokens, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateWriteConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bff.Sessions SET Tokens = @Tokens, AccessTokenExpiresAtUtc = @ExpiresAt, RenewedAtUtc = @Now WHERE SessionId = @SessionId;",
            new { SessionId = sessionId, Tokens = ProtectTokens(tokens), ExpiresAt = tokens.AccessTokenExpiresAtUtc, Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RemoveBySessionIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateWriteConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bff.Sessions WHERE SessionId = @SessionId;",
            new { SessionId = sessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Ends every session of a user (OIDC back-channel logout, all devices).</summary>
    public async Task<int> RemoveBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateWriteConnection();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bff.Sessions WHERE Subject = @Subject;",
            new { Subject = subject },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateWriteConnection();
        var total = 0;
        int deleted;
        do
        {
            // Small batches keep locks short on a hot table.
            deleted = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE TOP (1000) FROM bff.Sessions WHERE ExpiresAtUtc <= @Now;",
                new { Now = timeProvider.GetUtcNow() },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == 1000);

        return total;
    }

    public static string? GetSessionId(ClaimsPrincipal principal) => principal.FindFirst(SessionIdClaim)?.Value;

    /// <summary>Moves OAuth tokens out of the ticket properties so they are stored separately (and never in the ticket).</summary>
    private static SessionTokens? ExtractTokens(AuthenticationProperties properties)
    {
        var accessToken = properties.GetTokenValue("access_token");
        if (accessToken is null)
        {
            return null;
        }

        var expiresAt = DateTimeOffset.TryParse(properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        var tokens = new SessionTokens(accessToken, properties.GetTokenValue("refresh_token"), properties.GetTokenValue("id_token"), expiresAt);
        properties.StoreTokens([]);
        return tokens;
    }

    private byte[] ProtectTicket(AuthenticationTicket ticket) => _ticketProtector.Protect(TicketSerializer.Default.Serialize(ticket));

    private byte[] ProtectTokens(SessionTokens tokens) => _tokenProtector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens));

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static string Base64UrlEncode(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}

/// <summary>Deletes expired sessions in the background.</summary>
internal sealed partial class SessionCleanupService(
    SqlSessionStore store,
    IOptions<BffOptions> options,
    TimeProvider timeProvider,
    ILogger<SessionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SessionCleanupInterval, timeProvider);
        do
        {
            try
            {
                var removed = await store.RemoveExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    LogRemoved(logger, removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Background loop must survive transient database failures.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} expired BFF session(s)")]
    private static partial void LogRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "BFF session cleanup failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

internal static class CookieSchemes
{
    public const string Session = CookieAuthenticationDefaults.AuthenticationScheme;
}
