using System.Text.Json;
using EduEco.Api.Configuration;
using EduEco.Api.Security.Authorization;
using EduEco.Infrastructure.Security;
using EduEco.ServiceRegistry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Api.Security;

/// <summary>Calls the authorization server's introspection endpoint (RFC 7662) with a <c>private_key_jwt</c> assertion.</summary>
internal sealed partial class TokenIntrospectionService(
    IOptionsMonitor<JwtBearerOptions> jwtOptions,
    IOptions<ApiSecurityOptions> securityOptions,
    HybridCache cache,
    CertificateLoader certificates,
    TimeProvider timeProvider,
    ILogger<TokenIntrospectionService> logger)
{
    private readonly Lazy<SigningCredentials?> _credentials = new(() =>
        securityOptions.Value.Introspection is { } settings
        && (!string.IsNullOrWhiteSpace(settings.CertificatePath) || !string.IsNullOrWhiteSpace(settings.CertificateKeyVaultName))
            ? KeyMaterial.ToSigningCredentials(certificates.Load(settings.CertificatePath, settings.CertificatePassword, settings.CertificateKeyVaultName))
            : null);

    public bool Enabled => securityOptions.Value.Introspection.Enabled;

    public async Task<bool> IsActiveAsync(string accessToken, CancellationToken cancellationToken)
    {
        var settings = securityOptions.Value.Introspection;
        return await cache.GetOrCreateAsync(
            "introspection:" + KeyMaterial.Sha256Base64Url(accessToken),
            (service: this, accessToken),
            static async (state, ct) => await state.service.IntrospectAsync(state.accessToken, ct).ConfigureAwait(false),
            new HybridCacheEntryOptions
            {
                Expiration = settings.CacheDuration,
                LocalCacheExpiration = settings.CacheDuration,
                Flags = HybridCacheEntryFlags.DisableDistributedCache,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IntrospectAsync(string accessToken, CancellationToken cancellationToken)
    {
        var jwt = jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme);
        var configuration = await jwt.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var introspectionEndpoint = !string.IsNullOrEmpty(configuration.IntrospectionEndpoint)
            ? configuration.IntrospectionEndpoint
            : configuration.AdditionalData.TryGetValue("introspection_endpoint", out var endpoint) ? endpoint as string : null;
        if (string.IsNullOrEmpty(introspectionEndpoint))
        {
            throw new InvalidOperationException("The authorization server does not advertise an introspection endpoint.");
        }

        var settings = securityOptions.Value.Introspection;
        var credentials = _credentials.Value
            ?? throw new InvalidOperationException("Authentication:Introspection:CertificatePath or CertificateKeyVaultName is required when introspection is enabled.");

        var form = new Dictionary<string, string> { ["token"] = accessToken, ["token_type_hint"] = "access_token" };
        ClientAssertion.AddTo(form, settings.ClientId,
            ClientAssertion.Create(settings.ClientId, configuration.Issuer, credentials, timeProvider.GetUtcNow()));

        using var response = await jwt.Backchannel.PostAsync(introspectionEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Fail closed: a sensitive operation must not proceed on an unknown token state.
            LogIntrospectionFailed(logger, (int)response.StatusCode);
            return false;
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return json.RootElement.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Token introspection failed with {StatusCode}; treating token as inactive")]
    private static partial void LogIntrospectionFailed(ILogger logger, int statusCode);
}

public sealed class ActiveTokenRequirement : IAuthorizationRequirement
{
    public static readonly ActiveTokenRequirement Instance = new();
}

/// <summary>
/// High-risk endpoints: besides a valid signature, the token must still be active at the authorization server
/// (not revoked by logout, password reset or reuse detection). Enforced only when introspection is enabled.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireActiveTokenAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public RequireActiveTokenAttribute() => AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme;

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return ActiveTokenRequirement.Instance;
    }
}

internal sealed class ActiveTokenAuthorizationHandler(TokenIntrospectionService introspection) : AuthorizationHandler<ActiveTokenRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveTokenRequirement requirement)
    {
        if (!introspection.Enabled)
        {
            context.Succeed(requirement);
            return;
        }

        if (context.Resource is not HttpContext http
            || await http.GetTokenAsync(JwtBearerDefaults.AuthenticationScheme, "access_token") is not { Length: > 0 } token)
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureCodes.TokenRevoked));
            return;
        }

        if (await introspection.IsActiveAsync(token, http.RequestAborted))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureCodes.TokenRevoked));
        }
    }
}
