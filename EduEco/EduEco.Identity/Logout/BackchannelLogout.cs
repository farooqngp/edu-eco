using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using EduEco.Identity.Auditing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using ClientProperties = EduEco.Core.Authorization.ClientProperties;

namespace EduEco.Identity.Logout;

/// <summary>Queues "user signed out / credentials reset" events for back-channel delivery.</summary>
public sealed class BackchannelLogoutNotifier
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    internal ChannelReader<string> Reader => _queue.Reader;

    /// <summary>Ends every session of <paramref name="subject"/>: revokes its grants and notifies relying parties.</summary>
    public void Enqueue(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        _queue.Writer.TryWrite(subject);
    }
}

/// <summary>
/// Global logout (OpenID Connect Back-Channel Logout 1.0): revokes all valid authorizations (and their tokens) of the
/// subject, then POSTs a signed <c>logout_token</c> to every client of those authorizations that registered a
/// <c>backchannel_logout_uri</c>. Runs in the background so sign-out never waits on relying parties.
/// </summary>
internal sealed partial class BackchannelLogoutWorker(
    BackchannelLogoutNotifier notifier,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIddictServerOptions> serverOptions,
    TimeProvider timeProvider,
    ILogger<BackchannelLogoutWorker> logger) : BackgroundService
{
    public const string HttpClientName = "backchannel-logout";
    public const string LogoutTokenType = "logout+jwt";
    public const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var subject in notifier.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(subject, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed subject must not stop the worker.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogProcessingFailed(logger, ex, subject);
            }
        }
    }

    private async Task ProcessAsync(string subject, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        var applicationIds = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var authorization in authorizations.FindBySubjectAsync(subject, cancellationToken).ConfigureAwait(false))
        {
            if (!await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (await authorizations.GetApplicationIdAsync(authorization, cancellationToken).ConfigureAwait(false) is { } applicationId)
            {
                applicationIds.Add(applicationId);
            }

            var authorizationId = await authorizations.GetIdAsync(authorization, cancellationToken).ConfigureAwait(false);
            await authorizations.TryRevokeAsync(authorization, cancellationToken).ConfigureAwait(false);
            if (authorizationId is not null)
            {
                await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var applicationId in applicationIds)
        {
            var application = await applications.FindByIdAsync(applicationId, cancellationToken).ConfigureAwait(false);
            if (application is null)
            {
                continue;
            }

            var properties = await applications.GetPropertiesAsync(application, cancellationToken).ConfigureAwait(false);
            if (!properties.TryGetValue(ClientProperties.BackchannelLogoutUri, out var uriElement)
                || uriElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(uriElement.GetString(), UriKind.Absolute, out var logoutUri))
            {
                continue;
            }

            var clientId = await applications.GetClientIdAsync(application, cancellationToken).ConfigureAwait(false);
            await DeliverAsync(logoutUri, CreateLogoutToken(clientId!, subject), clientId!, cancellationToken).ConfigureAwait(false);
        }

        AuditLog.GlobalLogout(logger, subject, applicationIds.Count);
    }

    private string CreateLogoutToken(string clientId, string subject)
    {
        var options = serverOptions.CurrentValue;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer!.AbsoluteUri,
            Audience = clientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            TokenType = LogoutTokenType,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                ["events"] = new Dictionary<string, object> { [LogoutEvent] = new Dictionary<string, object>() },
            },
            SigningCredentials = options.SigningCredentials[0],
        });
    }

    private async Task DeliverAsync(Uri logoutUri, string logoutToken, string clientId, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.PostAsync(logoutUri, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["logout_token"] = logoutToken,
                }), cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    LogDelivered(logger, clientId);
                    return;
                }

                LogDeliveryRejected(logger, clientId, (int)response.StatusCode);
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    return; // Client rejected the token; retrying will not help.
                }
            }
            catch (HttpRequestException ex)
            {
                LogDeliveryFailed(logger, ex, clientId);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogDeliveryFailed(logger, ex, clientId);
            }

            if (attempt >= RetryDelays.Length)
            {
                return;
            }

            await Task.Delay(RetryDelays[attempt], timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Back-channel logout delivered to {ClientId}")]
    private static partial void LogDelivered(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-channel logout to {ClientId} rejected with {StatusCode}")]
    private static partial void LogDeliveryRejected(ILogger logger, string clientId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Back-channel logout to {ClientId} failed")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string clientId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Back-channel logout processing failed for subject {Subject}")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, string subject);
}
