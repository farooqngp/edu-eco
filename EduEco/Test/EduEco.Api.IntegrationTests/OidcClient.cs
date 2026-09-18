using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Api.IntegrationTests;

/// <summary>Minimal browser + confidential client driving the in-memory Identity server.</summary>
public sealed partial class OidcClient(HttpClient http)
{
    public async Task<TokenResult> SignInAsync(string email, string password, string clientId, string clientSecret, string scope, string? tenant)
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["redirect_uri"] = ApiFixture.RedirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "s",
        };
        if (tenant is not null)
        {
            parameters["tenant"] = tenant;
        }

        var response = await http.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", parameters));
        for (var hop = 0; hop < 10; hop++)
        {
            if (response.StatusCode != HttpStatusCode.Redirect && response.StatusCode != HttpStatusCode.Found)
            {
                throw new InvalidOperationException($"Authorization stopped with {(int)response.StatusCode}.");
            }

            var location = new Uri(http.BaseAddress!, response.Headers.Location!);
            if (location.Host != http.BaseAddress!.Host)
            {
                var query = QueryHelpers.ParseQuery(location.Query);
                if (!query.TryGetValue("code", out var code))
                {
                    throw new InvalidOperationException($"Authorization failed: {location}");
                }

                return await TokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code!,
                    ["redirect_uri"] = ApiFixture.RedirectUri,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["code_verifier"] = verifier,
                });
            }

            if (location.AbsolutePath.Equals("/Account/Login", StringComparison.OrdinalIgnoreCase))
            {
                var html = await http.GetStringAsync(location);
                var token = AntiforgeryRegex().Match(html).Groups[1].Value;
                response = await http.PostAsync(location, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Input.Email"] = email,
                    ["Input.Password"] = password,
                    ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
                }));
            }
            else
            {
                response = await http.GetAsync(location);
            }
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    public Task<TokenResult> ClientCredentialsAsync(string clientId, string secret, string scope) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope,
        });

    private async Task<TokenResult> TokenAsync(Dictionary<string, string> form)
    {
        using var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("access_token", out var accessToken)
            ? new TokenResult(accessToken.GetString()!)
            : throw new InvalidOperationException($"Token request failed: {body}");
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}

public sealed record TokenResult(string AccessToken);
