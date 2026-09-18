using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EduEco.Core.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Api.IntegrationTests;

/// <summary>Mints JWTs with the Identity server's signing certificate to probe individual validation rules.</summary>
internal static class TestTokens
{
    public static string Mint(
        X509Certificate2 signingCertificate,
        IDictionary<string, object> claims,
        string issuer = ApiFixture.Issuer,
        string audience = Resources.Api,
        string tokenType = "at+jwt",
        DateTime? expires = null,
        SigningCredentials? signingCredentials = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(5);
        var issuedAt = expiry < DateTime.UtcNow ? expiry.AddMinutes(-10) : DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expiry,
            TokenType = tokenType,
            SigningCredentials = signingCredentials ?? new X509SigningCredentials(signingCertificate),
        });
    }

    public static Dictionary<string, object> ServiceClaims(string clientId, string scope, long? tenantId = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = clientId, ["client_id"] = clientId, ["scope"] = scope };
        if (tenantId is not null)
        {
            claims[EduEcoClaimTypes.TenantId] = tenantId.Value.Invariant();
        }

        return claims;
    }

    public static Dictionary<string, object> UserClaims(long userId, long tenantId, string scope, params string[] roles) =>
        new()
        {
            ["sub"] = userId.Invariant(),
            ["client_id"] = ApiFixture.WebClientId,
            ["scope"] = scope,
            [EduEcoClaimTypes.TenantId] = tenantId.Invariant(),
            ["role"] = roles,
        };

    public static SigningCredentials ForeignKey() =>
        new(new RsaSecurityKey(RSA.Create(2048)) { KeyId = "attacker" }, SecurityAlgorithms.RsaSha256);
}
