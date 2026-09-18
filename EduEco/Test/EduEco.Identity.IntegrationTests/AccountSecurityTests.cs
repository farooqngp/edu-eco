using System.Net;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

[Collection(IdentityServerCollection.Name)]
public sealed class AccountSecurityTests(IdentityServerFixture fixture)
{
    [Fact]
    public async Task Login_page_sends_hardened_security_headers()
    {
        using var http = fixture.CreateClient();

        using var response = await http.GetAsync("/Account/Login", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        string.Join(';', response.Headers.GetValues("Content-Security-Policy")).ShouldContain("frame-ancestors 'none'");
        response.Headers.GetValues("Set-Cookie").ShouldAllBe(c => c.StartsWith("__Host-", StringComparison.Ordinal) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Session_cookie_is_host_prefixed_secure_and_httponly()
    {
        var user = await fixture.CreateUserAsync("cookie", [(fixture.TenantAlpha, Roles.Student)]);
        var client = new OidcTestClient(fixture.CreateClient());

        using var response = await client.LoginAsync(new Uri(client.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, IdentityServerFixture.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var session = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-EduEco.Session=", StringComparison.Ordinal));
        session.ShouldContain("secure", Case.Insensitive);
        session.ShouldContain("httponly", Case.Insensitive);
        session.ShouldContain("samesite=lax", Case.Insensitive);
        session.ShouldContain("path=/", Case.Insensitive);
        session.ShouldNotContain("domain=", Case.Insensitive);
    }

    [Fact]
    public async Task Wrong_password_does_not_reveal_account_existence_and_locks_out()
    {
        var user = await fixture.CreateUserAsync("lockout", [(fixture.TenantAlpha, Roles.Student)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var loginUrl = new Uri(client.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F");

        using (var unknown = await client.LoginAsync(loginUrl, "nobody@it.local", "Wrong!Password9"))
        using (var wrong = await client.LoginAsync(loginUrl, user.Email!, "Wrong!Password9"))
        {
            (await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Invalid sign-in attempt.");
            (await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Invalid sign-in attempt.");
        }

        HttpResponseMessage? last = null;
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            last?.Dispose();
            last = await client.LoginAsync(loginUrl, user.Email!, "Wrong!Password9");
        }

        last!.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        last.Headers.Location!.ToString().ShouldContain("/Account/Lockout");
        last.Dispose();

        // Correct password is refused while locked out.
        using var locked = await client.LoginAsync(loginUrl, user.Email!, IdentityServerFixture.Password);
        locked.Headers.Location!.ToString().ShouldContain("/Account/Lockout");
    }

    [Fact]
    public async Task Inactive_account_cannot_sign_in()
    {
        var user = await fixture.CreateUserAsync("inactive", [(fixture.TenantAlpha, Roles.Student)]);
        await fixture.UpdateUserAsync(user.Id, u => u.IsActive = false);
        var client = new OidcTestClient(fixture.CreateClient());

        using var response = await client.LoginAsync(new Uri(client.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, IdentityServerFixture.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Invalid sign-in attempt.");
    }

    [Fact]
    public async Task Login_return_url_cannot_redirect_off_site()
    {
        var user = await fixture.CreateUserAsync("openredirect", [(fixture.TenantAlpha, Roles.Student)]);
        var client = new OidcTestClient(fixture.CreateClient());

        using var response = await client.LoginAsync(
            new Uri(client.Http.BaseAddress!, "/Account/Login?ReturnUrl=https%3A%2F%2Fevil.example%2F"), user.Email!, IdentityServerFixture.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().ShouldBe("/");
    }

    [Fact]
    public async Task Passkey_sign_in_options_are_generated_without_user_hint()
    {
        var client = new OidcTestClient(fixture.CreateClient());
        var loginUrl = new Uri(client.Http.BaseAddress!, "/Account/Login");
        var token = await client.GetAntiforgeryTokenAsync(loginUrl);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login?handler=PasskeyOptions");
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("challenge").GetString().ShouldNotBeNullOrEmpty();
        json.RootElement.GetProperty("userVerification").GetString().ShouldBe("required");
        json.RootElement.TryGetProperty("allowCredentials", out var allow).ShouldBe(allow.ValueKind == System.Text.Json.JsonValueKind.Array);
        if (allow.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            allow.GetArrayLength().ShouldBe(0, "no account hint may leak before authentication");
        }
    }

    [Fact]
    public async Task Passkey_registration_options_require_authentication_and_target_issuer_domain()
    {
        var user = await fixture.CreateUserAsync("passkey", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());

        using (var anonymous = await client.Http.GetAsync("/Account/Manage/Passkeys", TestContext.Current.CancellationToken))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            anonymous.Headers.Location!.ToString().ShouldContain("/Account/Login");
        }

        using (await client.LoginAsync(new Uri(client.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, IdentityServerFixture.Password))
        {
        }

        var token = await client.GetAntiforgeryTokenAsync(new Uri(client.Http.BaseAddress!, "/Account/Manage/Passkeys"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Manage/Passkeys?handler=CreationOptions");
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("rp").GetProperty("id").GetString().ShouldBe("localhost");
        json.RootElement.GetProperty("user").GetProperty("name").GetString().ShouldBe(user.Email);
        json.RootElement.GetProperty("challenge").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Authenticator_enrolment_rejects_invalid_code()
    {
        var user = await fixture.CreateUserAsync("totp", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        using (await client.LoginAsync(new Uri(client.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, IdentityServerFixture.Password))
        {
        }

        var page = new Uri(client.Http.BaseAddress!, "/Account/Manage/TwoFactor");
        var html = await client.Http.GetStringAsync(page, TestContext.Current.CancellationToken);
        html.ShouldContain("otpauth://totp/");

        var token = await client.GetAntiforgeryTokenAsync(page);
        using var response = await client.Http.PostAsync("/Account/Manage/TwoFactor?handler=Enable",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "000000", ["__RequestVerificationToken"] = token }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("The verification code is invalid.");
    }

    [Fact]
    public async Task End_session_requires_confirmation_and_antiforgery()
    {
        var user = await fixture.CreateUserAsync("logout", [(fixture.TenantAlpha, Roles.Student)]);
        var client = new OidcTestClient(fixture.CreateClient());
        await client.SignInAsync(user.Email!);

        using var get = await client.Http.GetAsync("/connect/endsession", TestContext.Current.CancellationToken);
        get.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        get.Headers.Location!.ToString().ShouldStartWith("/Account/Logout");

        // Cross-site POST without antiforgery token is rejected (no CSRF logout).
        using var forged = await client.Http.PostAsync("/connect/endsession", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);
        forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var token = await client.GetAntiforgeryTokenAsync(new Uri(client.Http.BaseAddress!, get.Headers.Location));
        using var confirmed = await client.Http.PostAsync("/connect/endsession",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), TestContext.Current.CancellationToken);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Session cookie is gone: authorize now challenges for login again.
        using var authorize = await client.Http.GetAsync(OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge), TestContext.Current.CancellationToken);
        new Uri(client.Http.BaseAddress!, authorize.Headers.Location!).AbsolutePath.ShouldBe("/Account/Login");
    }
}
