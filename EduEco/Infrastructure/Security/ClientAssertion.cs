using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Infrastructure.Security;

/// <summary>
/// <c>private_key_jwt</c> client authentication (RFC 7523 §2.2, OIDC Core §9): a short-lived JWT signed with the client's
/// private key replaces the shared client secret. Only the public key is registered at the authorization server.
/// </summary>
public static class ClientAssertion
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>RFC 7523bis explicit type; prevents cross-JWT confusion.</summary>
    public const string TokenType = "client-authentication+jwt";

    /// <param name="audience">Authorization server issuer identifier (RFC 7523bis recommends the issuer).</param>
    public static string Create(
        string clientId,
        string audience,
        SigningCredentials signingCredentials,
        DateTimeOffset now,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + (lifetime ?? TimeSpan.FromMinutes(1))).UtcDateTime,
            TokenType = TokenType,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = clientId,
                ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            },
            SigningCredentials = signingCredentials,
        });
    }

    /// <summary>Adds <c>client_assertion_type</c> + <c>client_assertion</c> to a token/PAR/revocation form.</summary>
    public static void AddTo(IDictionary<string, string> form, string clientId, string assertion)
    {
        ArgumentNullException.ThrowIfNull(form);
        form["client_id"] = clientId;
        form["client_assertion_type"] = AssertionType;
        form["client_assertion"] = assertion;
    }
}
