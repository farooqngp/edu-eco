using EduEco.Infrastructure.Security;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace EduEco.Identity.Hosting;

/// <summary>
/// Makes <c>private_key_jwt</c> assertions single-use (RFC 7523 §3 item 7): each <c>jti</c> is accepted once per issuer,
/// and assertions must be short-lived. OpenIddict validates signature/audience/expiry but does not track <c>jti</c>.
/// </summary>
internal sealed class ClientAssertionReplayHandler(IReplayCache replayCache, TimeProvider timeProvider)
    : IOpenIddictServerHandler<ProcessAuthenticationContext>
{
    private const string CheckedProperty = ".eduEco_client_assertion_checked";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
            .UseScopedHandler<ClientAssertionReplayHandler>()
            .SetOrder(int.MaxValue - 50_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ClientAssertionPrincipal is not { } assertion
            || !context.Transaction.Properties.TryAdd(CheckedProperty, true))
        {
            return;
        }

        var jti = assertion.GetClaim(Claims.JwtId);
        var expiresAt = assertion.GetExpirationDate();
        var now = timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(jti) || expiresAt is null)
        {
            context.Reject(Errors.InvalidClient, "The client assertion must contain jti and exp.");
            return;
        }

        if (expiresAt.Value - now > MaximumLifetime)
        {
            context.Reject(Errors.InvalidClient, "The client assertion lifetime is too long.");
            return;
        }

        var issuer = assertion.GetClaim(Claims.Issuer) ?? context.ClientId;
        var remaining = expiresAt.Value - now + TimeSpan.FromMinutes(1); // cover clock skew tolerance
        if (!await replayCache.TryAddAsync($"client-assertion:{issuer}:{jti}", remaining).ConfigureAwait(false))
        {
            context.Reject(Errors.InvalidClient, "The client assertion has already been used.");
        }
    }
}
