using System.Globalization;
using System.Net;
using System.Text.Json;
using EduEco.Core.Authorization;
using EduEco.Identity.Logout;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

/// <summary>Back-channel logout, password reset, email confirmation and step-up re-authentication.</summary>
[Collection(IdentityServerCollection.Name)]
public sealed class AccountLifecycleTests(IdentityServerFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Logout_at_identity_revokes_grants_and_sends_signed_logout_tokens()
    {
        var user = await fixture.CreateUserAsync("slo-user", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var tokens = await client.SignInAsync(user.Email!);
        var subject = user.Id.ToString(CultureInfo.InvariantCulture);

        using (var page = await client.Http.GetAsync("/connect/endsession", Ct))
        {
            var token = await client.GetAntiforgeryTokenAsync(new Uri(client.Http.BaseAddress!, page.Headers.Location!));
            using var confirmed = await client.Http.PostAsync("/connect/endsession",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);
            confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        }

        var delivery = await WaitForAsync(() => fixture.BackchannelLogouts.Requests
            .Select(r => (r.Uri, Token: QueryHelpers.ParseQuery(r.Body)["logout_token"].ToString()))
            .FirstOrDefault(r => r.Token.Length > 0 && new JsonWebTokenHandler().ReadJsonWebToken(r.Token).Subject == subject));

        delivery.Uri.ShouldBe(new Uri(IdentityServerFixture.BackchannelLogoutUri));

        var jwks = new JsonWebKeySet(await client.Http.GetStringAsync("/.well-known/jwks", Ct));
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(delivery.Token, new TokenValidationParameters
        {
            ValidIssuer = IdentityServerFixture.Issuer,
            ValidAudience = IdentityServerFixture.WebClientId,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidTypes = [BackchannelLogoutWorker.LogoutTokenType],
        });
        validation.IsValid.ShouldBeTrue(validation.Exception?.Message);
        var logoutToken = (JsonWebToken)validation.SecurityToken;
        JsonDocument.Parse(logoutToken.GetClaim("events").Value).RootElement.TryGetProperty(BackchannelLogoutWorker.LogoutEvent, out _).ShouldBeTrue();
        logoutToken.TryGetClaim("nonce", out _).ShouldBeFalse();

        var refreshed = await WaitForAsync(async () =>
        {
            var result = await client.RefreshAsync(tokens.RefreshToken);
            return result.StatusCode == HttpStatusCode.BadRequest ? result : null;
        });
        refreshed.Error.ShouldBe("invalid_grant", "global logout revokes the user's grants");
    }

    [Fact]
    public async Task Password_reset_by_email_link_ends_existing_sessions()
    {
        var user = await fixture.CreateUserAsync("reset-user", [(fixture.TenantAlpha, Roles.Teacher)]);
        var session = new OidcTestClient(fixture.CreateClient());
        var tokens = await session.SignInAsync(user.Email!);

        var browser = new OidcTestClient(fixture.CreateClient());
        var forgot = new Uri(browser.Http.BaseAddress!, "/Account/ForgotPassword");
        using (await browser.Http.PostAsync(forgot, new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["email"] = user.Email!,
                   ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(forgot),
               }), Ct))
        {
        }

        var link = new Uri(fixture.Emails.LastLinkFor(user.Email!, "reset").ShouldNotBeNull());
        var resetPage = new Uri(browser.Http.BaseAddress!, link.PathAndQuery);
        var query = QueryHelpers.ParseQuery(link.Query);
        const string newPassword = "Brand!New9Password";

        using (var reset = await browser.Http.PostAsync(resetPage, new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["userId"] = query["userId"]!,
                   ["code"] = query["code"]!,
                   ["password"] = newPassword,
                   ["confirmPassword"] = newPassword,
                   ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(resetPage),
               }), Ct))
        {
            (await reset.Content.ReadAsStringAsync(Ct)).ShouldContain("Your password has been reset");
        }

        (await session.RefreshAsync(tokens.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var fresh = new OidcTestClient(fixture.CreateClient());
        (await fresh.LoginAsync(new Uri(fresh.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, IdentityServerFixture.Password))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "old password no longer works");
        (await fresh.LoginAsync(new Uri(fresh.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, newPassword))
            .StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Forgot_password_does_not_reveal_whether_an_account_exists()
    {
        var browser = new OidcTestClient(fixture.CreateClient());
        var forgot = new Uri(browser.Http.BaseAddress!, "/Account/ForgotPassword");
        var before = fixture.Emails.Sent.Count;

        using var response = await browser.Http.PostAsync(forgot, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "nobody-" + Guid.NewGuid().ToString("N") + "@it.local",
            ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(forgot),
        }), Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("If an account with a confirmed email address exists");
        fixture.Emails.Sent.Count.ShouldBe(before);
    }

    [Fact]
    public async Task Unconfirmed_account_can_sign_in_only_after_confirming_email()
    {
        var user = await fixture.CreateUserAsync("unconfirmed", [(fixture.TenantAlpha, Roles.Student)]);
        await fixture.UpdateUserAsync(user.Id, u => u.EmailConfirmed = false);
        var browser = new OidcTestClient(fixture.CreateClient());
        var login = new Uri(browser.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F");

        (await browser.LoginAsync(login, user.Email!, IdentityServerFixture.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var resend = new Uri(browser.Http.BaseAddress!, "/Account/ResendEmailConfirmation");
        using (await browser.Http.PostAsync(resend, new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["email"] = user.Email!,
                   ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(resend),
               }), Ct))
        {
        }

        var link = new Uri(fixture.Emails.LastLinkFor(user.Email!, "confirm").ShouldNotBeNull());
        (await browser.Http.GetStringAsync(link.PathAndQuery, Ct)).ShouldContain("Your email address is confirmed");

        (await browser.LoginAsync(login, user.Email!, IdentityServerFixture.Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Changing_second_factors_requires_recent_authentication()
    {
        var user = await fixture.CreateUserAsync("sudo-user", [(fixture.TenantAlpha, Roles.Teacher)]);
        var browser = new OidcTestClient(fixture.CreateClient());
        using (await browser.LoginAsync(new Uri(browser.Http.BaseAddress!, "/Account/Login?ReturnUrl=%2F"), user.Email!, IdentityServerFixture.Password))
        {
        }

        await Task.Delay(TimeSpan.FromSeconds(4), Ct); // test window is 3 seconds

        using var stale = await browser.Http.GetAsync("/Account/Manage/Passkeys", Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var reauth = new Uri(browser.Http.BaseAddress!, stale.Headers.Location!);
        reauth.AbsolutePath.ShouldBe("/Account/Reauthenticate");

        using (var wrong = await browser.Http.PostAsync(reauth, new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["password"] = "Wrong!Password9",
                   ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(reauth),
               }), Ct))
        {
            (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("The password is incorrect.");
        }

        using var ok = await browser.Http.PostAsync(reauth, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["password"] = IdentityServerFixture.Password,
            ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(reauth),
        }), Ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        ok.Headers.Location!.ToString().ShouldBe("/Account/Manage/Passkeys");

        using var fresh = await browser.Http.GetAsync("/Account/Manage/Passkeys", Ct);
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (probe() is { } value && !value.Equals(default(T)))
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition not met within 5 seconds.");
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition not met within 5 seconds.");
    }
}
