using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EduEco.Api.Configuration;
using EduEco.Core.Authorization;
using EduEco.Infrastructure.Security;
using EduEco.ServiceRegistry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Api.Security;

/// <summary>Registration outcome mirroring EduEco.Identity's <c>InternalRegistrationController</c> response (separate process, JSON contract only).</summary>
public enum RegistrationOutcome
{
    Created,
    AlreadyExists,
    ValidationFailed,
}

public sealed record InternalRegistrationResult(RegistrationOutcome Outcome, long? UserId, IReadOnlyList<string> Errors);

public sealed record TokenResult(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType);

/// <summary>
/// EduEco.Api's server-to-server calls into EduEco.Identity: internal JSON registration/password-reset (client-credentials,
/// scope <c>identity.internal</c>) and ROPC login/refresh (client <c>eduEco-api-ropc</c>). Reuses the same
/// <c>JwtBearerOptions.Backchannel</c>/<c>ConfigurationManager</c> already wired for token validation, so Identity's base
/// address and dev-certificate trust settings are configured in exactly one place.
/// </summary>
public sealed class IdentityInternalClient(
    IOptionsMonitor<JwtBearerOptions> jwtOptions,
    IOptions<ApiSecurityOptions> securityOptions,
    CertificateLoader certificates,
    TimeProvider timeProvider)
{
    private readonly Lazy<SigningCredentials> _credentials = new(() =>
    {
        var settings = securityOptions.Value.Introspection;
        return string.IsNullOrWhiteSpace(settings.CertificatePath) && string.IsNullOrWhiteSpace(settings.CertificateKeyVaultName)
            ? throw new InvalidOperationException("Authentication:Introspection:CertificatePath or CertificateKeyVaultName is required for server-to-server calls to Identity.")
            : KeyMaterial.ToSigningCredentials(certificates.Load(settings.CertificatePath, settings.CertificatePassword, settings.CertificateKeyVaultName));
    });

    public async Task<InternalRegistrationResult> RegisterAsync(
        string email, string? phoneNumber, string displayName, string password, CancellationToken cancellationToken)
    {
        var jwt = Jwt();
        var configuration = await jwt.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var token = await GetInternalApiTokenAsync(jwt, configuration.Issuer, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(configuration.Issuer), "internal/registrations"))
        {
            Content = JsonContent.Create(new { Email = email, PhoneNumber = phoneNumber, DisplayName = displayName, Password = password }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await jwt.Backchannel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InternalRegistrationResult>(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        var jwt = Jwt();
        var configuration = await jwt.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var token = await GetInternalApiTokenAsync(jwt, configuration.Issuer, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(configuration.Issuer), "internal/password-resets"))
        {
            Content = JsonContent.Create(new { Email = email }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await jwt.Backchannel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Password grant (RFC 6749 §4.3). Null return means invalid credentials (or a not-yet-confirmed/locked account).</summary>
    public Task<TokenResult?> PasswordSignInAsync(string identifier, string password, CancellationToken cancellationToken) =>
        TokenRequestAsync(
            new Dictionary<string, string> { ["grant_type"] = "password", ["username"] = identifier, ["password"] = password, ["scope"] = "openid profile email offline_access api.read api.write" },
            cancellationToken);

    public Task<TokenResult?> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        TokenRequestAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, cancellationToken);

    private async Task<TokenResult?> TokenRequestAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var jwt = Jwt();
        var configuration = await jwt.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var clientId = securityOptions.Value.RopcClientId;
        ClientAssertion.AddTo(form, clientId, ClientAssertion.Create(clientId, configuration.Issuer, _credentials.Value, timeProvider.GetUtcNow()));

        using var response = await jwt.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TokenResult>(cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<string> GetInternalApiTokenAsync(JwtBearerOptions jwt, string issuer, CancellationToken cancellationToken)
    {
        var clientId = securityOptions.Value.Introspection.ClientId;
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scopes.IdentityInternal };
        ClientAssertion.AddTo(form, clientId, ClientAssertion.Create(clientId, issuer, _credentials.Value, timeProvider.GetUtcNow()));

        var configuration = await jwt.ConfigurationManager!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        using var response = await jwt.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResult>(cancellationToken).ConfigureAwait(false);
        return token!.AccessToken;
    }

    private JwtBearerOptions Jwt() => jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme);
}
