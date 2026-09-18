using System.Net;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

/// <summary>Self-service registration: email confirmation, no enumeration, no token without a tenant membership.</summary>
[Collection(IdentityServerCollection.Name)]
public sealed class RegistrationTests(IdentityServerFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEmail(string label) => $"{label}-{Guid.NewGuid():N}@it.local";

    private async Task<(HttpStatusCode Status, string Html)> RegisterAsync(string email, string password, string? confirm = null)
    {
        var browser = new OidcTestClient(fixture.CreateClient());
        var page = new Uri(browser.Http.BaseAddress!, "/Account/Register");
        using var response = await browser.Http.PostAsync(page, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.DisplayName"] = "Registered User",
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = confirm ?? password,
            ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(page),
        }), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Registered_user_must_confirm_email_and_get_a_membership_before_tokens_are_issued()
    {
        var email = NewEmail("register");
        var (status, html) = await RegisterAsync(email, IdentityServerFixture.Password);
        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Check your email");

        var user = (await fixture.FindUserByEmailAsync(email)).ShouldNotBeNull();
        user.EmailConfirmed.ShouldBeFalse();

        // Unconfirmed: sign-in is refused, so the authorization flow never reaches the client.
        var unconfirmed = new OidcTestClient(fixture.CreateClient());
        var (_, redirect) = await unconfirmed.FollowAsync(OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge), email);
        redirect.ShouldBeNull();

        // Confirm through the emailed link.
        var link = new Uri(fixture.Emails.LastLinkFor(email, "confirm").ShouldNotBeNull());
        var confirmer = new OidcTestClient(fixture.CreateClient());
        (await confirmer.Http.GetStringAsync(link.PathAndQuery, Ct)).ShouldContain("Your email address is confirmed");
        (await fixture.FindUserByEmailAsync(email)).ShouldNotBeNull().EmailConfirmed.ShouldBeTrue();

        // Confirmed but no tenant membership: authorization is denied.
        var noTenant = new OidcTestClient(fixture.CreateClient());
        var (_, denied) = await noTenant.FollowAsync(OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge), email);
        QueryHelpers.ParseQuery(denied.ShouldNotBeNull().Query)["error"].ToString().ShouldBe("access_denied");

        // An administrator grants access: tokens are issued for that tenant.
        await fixture.AddMembershipAsync(user.Id, fixture.TenantAlpha, Roles.Student);
        var tokens = await new OidcTestClient(fixture.CreateClient()).SignInAsync(email);
        tokens.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Registering_an_existing_address_gives_the_same_response_and_sends_nothing()
    {
        var existing = await fixture.CreateUserAsync("already-registered");
        var sentBefore = fixture.Emails.Sent.Count(m => m.To == existing.Email);

        var (status, html) = await RegisterAsync(existing.Email!, IdentityServerFixture.Password);

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Check your email");
        fixture.Emails.Sent.Count(m => m.To == existing.Email).ShouldBe(sentBefore);
    }

    [Theory]
    [InlineData("short1!A", null, "minimum length of 12")]
    [InlineData("LongEnough-Passw0rd!", "Different-Passw0rd!", "do not match")]
    public async Task Invalid_registration_input_is_rejected_without_creating_an_account(string password, string? confirm, string message)
    {
        var email = NewEmail("invalid-register");

        var (status, html) = await RegisterAsync(email, password, confirm);

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldNotContain("Check your email");
        html.ShouldContain(message, Case.Insensitive);
        (await fixture.FindUserByEmailAsync(email)).ShouldBeNull();
    }
}
