using EduEco.Bff.Configuration;
using EduEco.Infrastructure.Security;
using EduEco.ServiceRegistry;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Bff.Tokens;

/// <summary>
/// Authenticates the BFF (confidential client) at the token, PAR and revocation endpoints: <c>private_key_jwt</c> when a
/// client certificate is configured (required in production), otherwise the development client secret.
/// Assertions always use wall-clock time: the authorization server judges their validity window with its own clock.
/// </summary>
public sealed class BffClientAuthentication(IOptions<BffOptions> options, CertificateLoader certificates)
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private readonly Lazy<SigningCredentials?> _credentials = new(() =>
        options.Value.ClientAssertion is { IsConfigured: true } assertion
            ? KeyMaterial.ToSigningCredentials(certificates.Load(assertion.CertificatePath, assertion.CertificatePassword, assertion.KeyVaultName))
            : null);

    public bool UsesPrivateKeyJwt => _credentials.Value is not null;

    /// <summary>Adds client credentials to a form post (refresh, revocation).</summary>
    public void Apply(IDictionary<string, string> form, string issuer)
    {
        ArgumentNullException.ThrowIfNull(form);
        var clientId = options.Value.ClientId;

        if (_credentials.Value is { } credentials)
        {
            ClientAssertion.AddTo(form, clientId, ClientAssertion.Create(clientId, issuer, credentials, Clock.GetUtcNow()));
            return;
        }

        form["client_id"] = clientId;
        form["client_secret"] = options.Value.ClientSecret!;
    }

    /// <summary>Replaces the secret with an assertion on a protocol message (code redemption, PAR).</summary>
    public void Apply(OpenIdConnectMessage message, string issuer)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_credentials.Value is not { } credentials)
        {
            return;
        }

        message.ClientId = options.Value.ClientId;
        message.ClientSecret = null;
        message.ClientAssertionType = ClientAssertion.AssertionType;
        message.ClientAssertion = ClientAssertion.Create(options.Value.ClientId, issuer, credentials, Clock.GetUtcNow());
    }
}
