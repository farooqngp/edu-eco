using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Identity.IntegrationTests;

internal static class Phase5Helpers
{
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string AuthorizeUrl(string clientId, string redirectUri, string scope, string challenge, IDictionary<string, string?>? extra = null)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "s",
        };

        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            parameters[key] = value;
        }

        return QueryHelpers.AddQueryString("/connect/authorize", parameters);
    }

    /// <summary>Signs in through the browser flow and returns the authorization code (throws on error redirect).</summary>
    public static async Task<string> GetCodeAsync(OidcTestClient client, string authorizeUrl, string email)
    {
        var (response, redirect) = await client.FollowAsync(authorizeUrl, email);
        if (redirect is null)
        {
            throw new InvalidOperationException($"No client redirect: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        var query = QueryHelpers.ParseQuery(redirect.Query);
        return query.TryGetValue("code", out var code) ? code.ToString() : throw new InvalidOperationException($"Authorization failed: {redirect}");
    }

    public static Uri TokenEndpoint => new(new Uri(IdentityServerFixture.Issuer), "connect/token");
}
