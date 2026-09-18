using EduEco.Identity.Configuration;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace EduEco.Identity.Hosting;

/// <summary>
/// Removes expired/revoked tokens and orphaned authorizations so auth.OpenIddictTokens stays bounded.
/// Threshold exceeds the absolute refresh lifetime so audit-relevant chains are not pruned early.
/// </summary>
internal sealed partial class TokenPruningService(
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityServerOptions> options,
    TimeProvider timeProvider,
    ILogger<TokenPruningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.TokenPruningInterval, timeProvider);
        do
        {
            try
            {
                var threshold = timeProvider.GetUtcNow() - options.Value.RefreshTokenAbsoluteLifetime - TimeSpan.FromDays(1);

                await using var scope = scopeFactory.CreateAsyncScope();
                var tokens = await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
                    .PruneAsync(threshold, stoppingToken).ConfigureAwait(false);
                var authorizations = await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
                    .PruneAsync(threshold, stoppingToken).ConfigureAwait(false);

                LogPruned(logger, tokens, authorizations);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Background loop must survive transient database failures.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPruneFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} token(s) and {Authorizations} authorization(s)")]
    private static partial void LogPruned(ILogger logger, long tokens, long authorizations);

    [LoggerMessage(Level = LogLevel.Error, Message = "Token pruning failed")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
