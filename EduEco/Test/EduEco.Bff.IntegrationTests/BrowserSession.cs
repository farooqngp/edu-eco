using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EduEco.Bff.IntegrationTests;

/// <summary>
/// Simulates a browser: one cookie jar for the BFF origin, one for the Identity origin, redirects followed by hand.
/// </summary>
internal sealed partial class BrowserSession(BffFixture fixture, bool instanceB = false)
{
    public HttpClient Bff { get; } = fixture.CreateBffClient(instanceB);

    private HttpClient IdentityClient { get; } = fixture.CreateIdentityClient();

    /// <summary>Set-Cookie headers of the OIDC callback response (session cookie flags).</summary>
    public IReadOnlyList<string> CallbackSetCookies { get; private set; } = [];

    /// <summary>Raw value of the session cookie, for replay tests.</summary>
    public string SessionCookieValue { get; private set; } = string.Empty;

    /// <summary>Location of the first redirect to the Identity server's authorization endpoint.</summary>
    public Uri? AuthorizationRequest { get; private set; }

    /// <summary>Follows a redirect to the Identity server with this browser's Identity cookies.</summary>
    public Task<HttpResponseMessage> FollowIdentityAsync(Uri location) => IdentityClient.GetAsync(location);

    /// <summary>Runs /bff/login through the Identity login form and back to the BFF callback.</summary>
    public async Task SignInAsync(string email, string? tenant = null, string returnUrl = "/")
    {
        var loginUrl = tenant is null
            ? $"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : $"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}&tenant={Uri.EscapeDataString(tenant)}";

        using var challenge = await Bff.GetAsync(loginUrl);
        challenge.StatusCode.ShouldBeRedirect();

        var location = challenge.Headers.Location!;
        AuthorizationRequest = location;
        for (var hop = 0; hop < 10; hop++)
        {
            if (location.Host == new Uri(BffFixture.BffOrigin).Host)
            {
                using var callback = await Bff.GetAsync(location);
                callback.StatusCode.ShouldBeRedirect($"callback failed: {callback.StatusCode}");

                CallbackSetCookies = callback.Headers.TryGetValues("Set-Cookie", out var cookies)
                    ? [.. cookies.Where(c => c.StartsWith(BffFixture.SessionCookieName + "=", StringComparison.Ordinal))]
                    : [];
                SessionCookieValue = CallbackSetCookies.Count == 1
                    ? CallbackSetCookies[0].Split(';')[0][(BffFixture.SessionCookieName.Length + 1)..]
                    : string.Empty;
                return;
            }

            using var response = location.AbsolutePath.Equals("/Account/Login", StringComparison.OrdinalIgnoreCase)
                ? await PostLoginAsync(location, email)
                : await IdentityClient.GetAsync(location);

            response.StatusCode.ShouldBeRedirect($"identity stopped at {location} with {response.StatusCode}");
            location = new Uri(new Uri(BffFixture.IdentityIssuer), response.Headers.Location!);
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    public Task<HttpResponseMessage> GetAsync(string path, bool csrf = true) => SendAsync(HttpMethod.Get, path, csrf);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }

        return await Bff.SendAsync(request);
    }

    public async Task<JsonElement?> GetUserAsync()
    {
        using var response = await GetAsync("/bff/user");
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<JsonElement>() : null;
    }

    private async Task<HttpResponseMessage> PostLoginAsync(Uri loginUrl, string email)
    {
        var html = await IdentityClient.GetStringAsync(loginUrl);
        var token = WebUtility.HtmlDecode(AntiforgeryRegex().Match(html).Groups[1].Value);
        return await IdentityClient.PostAsync(loginUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = BffFixture.Password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}

internal static class StatusCodeAssertions
{
    public static void ShouldBeRedirect(this HttpStatusCode statusCode, string? because = null)
    {
        if (statusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther))
        {
            throw new InvalidOperationException(because ?? $"Expected a redirect but got {statusCode}.");
        }
    }
}
