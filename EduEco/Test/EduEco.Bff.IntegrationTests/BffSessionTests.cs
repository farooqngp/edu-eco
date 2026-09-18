using System.Net;
using System.Text.Json;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Bff.IntegrationTests;

[Collection(BffCollection.Name)]
public sealed class BffSessionTests(BffFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_user_endpoint_returns_401_and_missing_csrf_header_returns_403()
    {
        var browser = new BrowserSession(fixture);

        using (var withHeader = await browser.GetAsync("/bff/user"))
        {
            withHeader.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var withoutHeader = await browser.GetAsync("/bff/user", csrf: false);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await withoutHeader.Content.ReadAsStringAsync(Ct)).ShouldContain("csrf_header_required");
    }

    [Fact]
    public async Task Login_creates_a_server_side_session_and_the_browser_gets_only_an_opaque_cookie()
    {
        var user = await fixture.CreateUserAsync("spa-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);

        await browser.SignInAsync(user.Email!);

        var body = (await browser.GetUserAsync()).ShouldNotBeNull();
        body.GetProperty("subject").GetString().ShouldBe(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        body.GetProperty("tenantId").GetInt64().ShouldBe(fixture.TenantAlpha.Id);
        var raw = body.GetRawText();
        raw.ShouldNotContain("access_token");
        raw.ShouldNotContain("refresh_token");
        raw.ShouldNotContain("id_token");

        // Encrypted envelope around one session key (~300 chars). A ticket carrying access + refresh + id tokens
        // would be several kilobytes and would usually be chunked into .AspNetCore.*C1/C2 cookies.
        browser.SessionCookieValue.Length.ShouldBeLessThan(600, "the cookie must not carry tokens");
        browser.CallbackSetCookies.ShouldHaveSingleItem();

        var sessionId = SessionIdFrom(body);
        (await fixture.GetSessionAsync(sessionId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Api_calls_are_proxied_with_the_users_access_token()
    {
        var user = await fixture.CreateUserAsync("proxy-user", (fixture.TenantAlpha, Roles.TenantAdmin));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        using var response = await browser.GetAsync("/api/v1/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("userId").GetInt64().ShouldBe(user.Id);
        json.RootElement.GetProperty("isServiceClient").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("tenantId").GetInt64().ShouldBe(fixture.TenantAlpha.Id);
        json.RootElement.GetProperty("effectivePermissions").EnumerateArray().Select(p => p.GetString())
            .ShouldContain(Permissions.Users.Manage);

        // Permission enforcement still happens at the API, not at the BFF.
        using var forbidden = await browser.GetAsync("/api/v1/tenants");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Proxy_requires_csrf_header_and_a_session()
    {
        var user = await fixture.CreateUserAsync("guard-user", (fixture.TenantAlpha, Roles.Teacher));

        var anonymous = new BrowserSession(fixture);
        using (var withoutSession = await anonymous.GetAsync("/api/v1/me"))
        {
            withoutSession.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        using var withoutHeader = await browser.GetAsync("/api/v1/me", csrf: false);
        withoutHeader.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await withoutHeader.Content.ReadAsStringAsync(Ct)).ShouldContain("csrf_header_required");
    }

    [Fact]
    public async Task Expired_access_tokens_are_refreshed_transparently()
    {
        var user = await fixture.CreateUserAsync("refresh-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        var sessionId = SessionIdFrom((await browser.GetUserAsync()).ShouldNotBeNull());
        var before = (await fixture.GetSessionAsync(sessionId)).AccessTokenExpiresAtUtc.ShouldNotBeNull();

        fixture.BffClock.Advance(TimeSpan.FromMinutes(10)); // past the 5 minute access token lifetime

        using var response = await browser.GetAsync("/api/v1/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        var after = (await fixture.GetSessionAsync(sessionId)).AccessTokenExpiresAtUtc.ShouldNotBeNull();
        after.ShouldBeGreaterThan(before, "the refresh token grant produced a new access token");
    }

    [Fact]
    public async Task Concurrent_requests_refresh_only_once_and_do_not_trip_reuse_detection()
    {
        var user = await fixture.CreateUserAsync("concurrent-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        fixture.BffClock.Advance(TimeSpan.FromMinutes(10));

        // Identity revokes the whole chain if the same refresh token is redeemed twice.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => browser.GetAsync("/api/v1/me")));
        try
        {
            responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        using var afterwards = await browser.GetAsync("/api/v1/me");
        afterwards.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_ends_the_session_everywhere_and_a_replayed_cookie_is_useless()
    {
        var user = await fixture.CreateUserAsync("logout-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        var body = (await browser.GetUserAsync()).ShouldNotBeNull();
        var sessionId = SessionIdFrom(body);
        var cookie = browser.SessionCookieValue;

        using (var wrongSid = await browser.GetAsync("/bff/logout?sid=not-the-session"))
        {
            wrongSid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await fixture.GetSessionAsync(sessionId)).Count.ShouldBe(1, "a forged logout link must not end the session");
        }

        using var logout = await browser.GetAsync(body.GetProperty("logoutUrl").GetString()!);
        logout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logout.Headers.Location!.ToString().ShouldContain("/connect/endsession");
        logout.Headers.Location!.ToString().ShouldContain("id_token_hint=");

        (await fixture.GetSessionAsync(sessionId)).Count.ShouldBe(0);

        // Replaying the old cookie on a fresh client: the server-side session is gone, so it authenticates nothing.
        using var replay = fixture.CreateBffClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/user");
        request.Headers.Add("Cookie", $"{BffFixture.SessionCookieName}={cookie}");
        request.Headers.Add("X-CSRF", "1");
        using var replayed = await replay.SendAsync(request, Ct);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenant_can_be_selected_at_login()
    {
        var user = await fixture.CreateUserAsync("multi-user", (fixture.TenantAlpha, Roles.Teacher), (fixture.TenantBeta, Roles.Student));
        var browser = new BrowserSession(fixture);

        await browser.SignInAsync(user.Email!, tenant: fixture.TenantBeta.Code);

        (await browser.GetUserAsync()).ShouldNotBeNull().GetProperty("tenantId").GetInt64().ShouldBe(fixture.TenantBeta.Id);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example")]
    public async Task Login_rejects_non_local_return_urls(string returnUrl)
    {
        var browser = new BrowserSession(fixture);

        using var response = await browser.GetAsync($"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Session_cookie_is_host_prefixed_secure_httponly_and_strict()
    {
        var user = await fixture.CreateUserAsync("cookie-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        using var response = await browser.GetAsync("/bff/user");

        // Flags asserted from the Set-Cookie header captured during the OIDC callback.
        var session = browser.CallbackSetCookies.ShouldHaveSingleItem();
        session.ShouldStartWith(BffFixture.SessionCookieName + "=");
        session.ShouldContain("secure", Case.Insensitive);
        session.ShouldContain("httponly", Case.Insensitive);
        session.ShouldContain("samesite=strict", Case.Insensitive);
        session.ShouldContain("path=/", Case.Insensitive);
        session.ShouldNotContain("domain=", Case.Insensitive);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string SessionIdFrom(JsonElement user) =>
        user.GetProperty("logoutUrl").GetString()!.Split("sid=")[1];
}
