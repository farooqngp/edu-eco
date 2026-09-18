using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Identity.IntegrationTests;

/// <summary>Drives the authorization server over HTTP exactly like a browser + confidential client would.</summary>
internal sealed partial class OidcTestClient(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string AuthorizeUrl(
        string? codeChallenge,
        string? tenant = null,
        string scope = "openid profile email offline_access api.read",
        string codeChallengeMethod = "S256",
        string clientId = IdentityServerFixture.WebClientId)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["redirect_uri"] = IdentityServerFixture.RedirectUri,
            ["state"] = "state-123",
            ["nonce"] = "nonce-123",
        };

        if (codeChallenge is not null)
        {
            parameters["code_challenge"] = codeChallenge;
            parameters["code_challenge_method"] = codeChallengeMethod;
        }

        if (tenant is not null)
        {
            parameters["tenant"] = tenant;
        }

        return QueryHelpers.AddQueryString("/connect/authorize", parameters);
    }

    /// <summary>
    /// Follows local redirects (performing the login form when challenged) until the server redirects back to the client
    /// or returns a non-redirect response.
    /// </summary>
    public async Task<(HttpResponseMessage Response, Uri? ClientRedirect)> FollowAsync(string url, string email, string password = IdentityServerFixture.Password)
    {
        var response = await Http.GetAsync(url);
        for (var hop = 0; hop < 10; hop++)
        {
            if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther))
            {
                return (response, null);
            }

            var location = new Uri(Http.BaseAddress!, response.Headers.Location!);
            if (!string.Equals(location.Host, Http.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase))
            {
                return (response, location);
            }

            response = location.AbsolutePath.Equals("/Account/Login", StringComparison.OrdinalIgnoreCase)
                ? await LoginAsync(location, email, password)
                : await Http.GetAsync(location);
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    public async Task<HttpResponseMessage> LoginAsync(Uri loginUrl, string email, string password)
    {
        var token = await GetAntiforgeryTokenAsync(loginUrl);
        return await Http.PostAsync(loginUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    public async Task<string> GetAntiforgeryTokenAsync(Uri pageUrl)
    {
        var html = await Http.GetStringAsync(pageUrl);
        return AntiforgeryRegex().Match(html) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : throw new InvalidOperationException($"No antiforgery token on {pageUrl}.");
    }

    /// <summary>Full interactive flow: authorize → login → (tenant) → code → tokens.</summary>
    public async Task<TokenResponse> SignInAsync(string email, string? tenant = null, string scope = "openid profile email offline_access api.read")
    {
        var (verifier, challenge) = CreatePkce();
        var (response, redirect) = await FollowAsync(AuthorizeUrl(challenge, tenant, scope), email);
        if (redirect is null)
        {
            throw new InvalidOperationException($"Authorization did not redirect to the client: {(int)response.StatusCode} {response.Headers.Location}");
        }

        var query = QueryHelpers.ParseQuery(redirect.Query);
        if (!query.TryGetValue("code", out var code))
        {
            throw new InvalidOperationException($"Authorization failed: {redirect}");
        }

        return await RedeemCodeAsync(code!, verifier);
    }

    public async Task<TokenResponse> RedeemCodeAsync(string code, string verifier) =>
        await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = IdentityServerFixture.RedirectUri,
            ["client_id"] = IdentityServerFixture.WebClientId,
            ["client_secret"] = IdentityServerFixture.WebClientSecret,
            ["code_verifier"] = verifier,
        });

    public Task<TokenResponse> RefreshAsync(string refreshToken) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = IdentityServerFixture.WebClientId,
            ["client_secret"] = IdentityServerFixture.WebClientSecret,
        });

    public Task<TokenResponse> ClientCredentialsAsync(string clientId, string secret, string scope) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope,
        });

    public async Task<TokenResponse> TokenAsync(IDictionary<string, string> form, string? dpopProof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = new FormUrlEncodedContent(form) };
        if (dpopProof is not null)
        {
            request.Headers.Add("DPoP", dpopProof);
        }

        using var response = await Http.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new TokenResponse(response.StatusCode, json.RootElement.Clone());
    }

    /// <summary>Validates an access token with the published JWKS, as a resource server would.</summary>
    public async Task<JsonWebToken> ValidateAccessTokenAsync(string accessToken)
    {
        var jwks = new JsonWebKeySet(await Http.GetStringAsync("/.well-known/jwks"));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = IdentityServerFixture.Issuer,
            ValidAudience = EduEco.Core.Authorization.Resources.Api,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = ["at+jwt"],
        });

        return result.IsValid
            ? (JsonWebToken)result.SecurityToken
            : throw new InvalidOperationException("Access token validation failed.", result.Exception);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}

internal sealed record TokenResponse(HttpStatusCode StatusCode, JsonElement Body)
{
    public string? Get(string name) => Body.TryGetProperty(name, out var value) ? value.GetString() : null;

    public string AccessToken => Get("access_token") ?? throw new InvalidOperationException($"No access_token: {Body}");

    public string RefreshToken => Get("refresh_token") ?? throw new InvalidOperationException($"No refresh_token: {Body}");

    public string? Error => Get("error");
}
