using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using EduEco.Core.Authorization;
using EduEco.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace EduEco.Bff.IntegrationTests;

/// <summary>PAR (RFC 9126), private_key_jwt (RFC 7523), OIDC back-channel logout, cluster-wide refresh lock.</summary>
[Collection(BffCollection.Name)]
public sealed class BffHardeningTests(BffFixture fixture)
{
    private static readonly Uri BackchannelLogoutUri = new(BffFixture.BackchannelLogoutUri);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_pushes_the_authorization_request_and_authenticates_with_private_key_jwt()
    {
        var user = await fixture.CreateUserAsync("par-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!, fixture.TenantAlpha.Code);

        // The browser only carries client_id + request_uri: PKCE challenge, scopes, tenant and redirect_uri stay on the back channel.
        var query = browser.AuthorizationRequest.ShouldNotBeNull().Query;
        query.ShouldContain("request_uri=" + Uri.EscapeDataString("urn:ietf:params:oauth:request_uri:"));
        query.ShouldNotContain("code_challenge");
        query.ShouldNotContain("redirect_uri");
        query.ShouldNotContain("tenant");

        // The client has no secret: code redemption succeeded with a signed client assertion.
        var body = (await browser.GetUserAsync()).ShouldNotBeNull();
        body.GetProperty("tenantId").GetInt64().ShouldBe(fixture.TenantAlpha.Id);
    }

    [Fact]
    public async Task Signing_out_at_the_identity_server_ends_the_users_sessions_in_every_browser()
    {
        var user = await fixture.CreateUserAsync("backchannel-user", (fixture.TenantAlpha, Roles.Teacher));
        var laptop = new BrowserSession(fixture);
        var tablet = new BrowserSession(fixture, instanceB: true);
        await laptop.SignInAsync(user.Email!);
        await tablet.SignInAsync(user.Email!);
        (await fixture.CountSessionsAsync(user.Id)).ShouldBe(2);

        var body = (await laptop.GetUserAsync()).ShouldNotBeNull();
        using var logout = await laptop.GetAsync(body.GetProperty("logoutUrl").GetString()!);
        using var endSession = await laptop.FollowIdentityAsync(logout.Headers.Location!);
        endSession.StatusCode.ShouldBeRedirect($"end session returned {endSession.StatusCode}");

        // Identity notifies the BFF asynchronously (logout+jwt to /bff/backchannel-logout).
        var remaining = -1;
        for (var attempt = 0; attempt < 50 && remaining != 0; attempt++)
        {
            remaining = await fixture.CountSessionsAsync(user.Id);
            if (remaining != 0)
            {
                await Task.Delay(100, Ct);
            }
        }

        remaining.ShouldBe(0);
        (await tablet.GetUserAsync()).ShouldBeNull("the session on the other device was ended by back-channel logout");
    }

    [Fact]
    public async Task Valid_logout_token_is_accepted_once_and_replay_is_rejected()
    {
        var user = await fixture.CreateUserAsync("logout-token-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        var token = LogoutToken(user.Id.ToString(CultureInfo.InvariantCulture));
        using (var first = await PostLogoutTokenAsync(token))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            first.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        }

        (await fixture.CountSessionsAsync(user.Id)).ShouldBe(0);

        using var replay = await PostLogoutTokenAsync(token);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    public static TheoryData<string> BadLogoutTokens =>
        ["garbage", "wrong-key", "wrong-audience", "wrong-issuer", "wrong-type", "no-event", "with-nonce", "no-sub", "expired"];

    [Theory]
    [MemberData(nameof(BadLogoutTokens))]
    public async Task Invalid_logout_tokens_are_rejected_and_sessions_survive(string scenario)
    {
        var user = await fixture.CreateUserAsync("bad-logout-" + scenario, (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);
        var subject = user.Id.ToString(CultureInfo.InvariantCulture);

        using var otherKey = RSA.Create(2048);
        var token = scenario switch
        {
            "garbage" => "not-a-jwt",
            "wrong-key" => LogoutToken(subject, signingKey: new RsaSecurityKey(otherKey)),
            "wrong-audience" => LogoutToken(subject, audience: "someone-else"),
            "wrong-issuer" => LogoutToken(subject, issuer: "https://evil.test/"),
            "wrong-type" => LogoutToken(subject, type: "JWT"),
            "no-event" => LogoutToken(subject, includeEvent: false),
            "with-nonce" => LogoutToken(subject, extra: new Dictionary<string, object> { ["nonce"] = "n" }),
            "no-sub" => LogoutToken(null),
            _ => LogoutToken(subject, issuedAt: DateTime.UtcNow.AddMinutes(-10)),
        };

        using var response = await PostLogoutTokenAsync(token);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, scenario);
        (await fixture.CountSessionsAsync(user.Id)).ShouldBe(1, scenario);
    }

    [Fact]
    public async Task Concurrent_refreshes_across_instances_redeem_the_refresh_token_once()
    {
        var user = await fixture.CreateUserAsync("scale-refresh-user", (fixture.TenantAlpha, Roles.Teacher));
        var browser = new BrowserSession(fixture);
        await browser.SignInAsync(user.Email!);

        // The same session cookie reaches both instances (round-robin load balancing, shared key ring).
        using var instanceB = fixture.CreateBffClient(instanceB: true);
        fixture.BffClock.Advance(TimeSpan.FromMinutes(10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
        {
            if (i % 2 == 0)
            {
                return browser.GetAsync("/api/v1/me");
            }

#pragma warning disable CA2000 // Disposed with its response below.
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
#pragma warning restore CA2000
            request.Headers.Add("Cookie", $"{BffFixture.SessionCookieName}={browser.SessionCookieValue}");
            request.Headers.Add("X-CSRF", "1");
            return instanceB.SendAsync(request, Ct);
        }));

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

        // Refresh token reuse detection would have revoked the chain and ended the session.
        using var afterwards = await browser.GetAsync("/api/v1/me");
        afterwards.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> PostLogoutTokenAsync(string token)
    {
        using var client = fixture.CreateBffClient();
        return await client.PostAsync(
            BackchannelLogoutUri,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }),
            Ct);
    }

    private string LogoutToken(
        string? subject,
        SecurityKey? signingKey = null,
        string audience = BffFixture.BffClientId,
        string issuer = BffFixture.IdentityIssuer,
        string type = "logout+jwt",
        bool includeEvent = true,
        DateTime? issuedAt = null,
        Dictionary<string, object>? extra = null)
    {
        var claims = new Dictionary<string, object>(extra ?? [])
        {
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (includeEvent)
        {
            claims["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>(),
            };
        }

        var credentials = signingKey is null
            ? KeyMaterial.ToSigningCredentials(KeyMaterial.LoadPkcs12(fixture.SigningCertificatePath, BffFixture.SigningCertificatePassword))
            : new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        var iat = issuedAt ?? DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = iat,
            NotBefore = iat,
            Expires = iat.AddMinutes(2),
            TokenType = type,
            SigningCredentials = credentials,
        });
    }
}
