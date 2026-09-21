using System.Net;
using System.Net.Http.Json;
using EduEco.Core.Authorization;
using EduEco.Identity.Controllers;
using EduEco.Infrastructure.Security;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

/// <summary>
/// Server-to-server provisioning used by EduEco.Api's <c>POST api/v1/tenants</c>. Unlike self-registration, the
/// account is created already confirmed (an unconfirmed user could neither sign in nor be given a membership) with a
/// password nobody is told; the invitee sets their own through the emailed reset link.
/// </summary>
[Collection(IdentityServerCollection.Name)]
public sealed class InternalTenantAdminTests(IdentityServerFixture fixture)
{
    private const string LoginUrl = "http://localhost:4200/login";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> InternalClientAsync()
    {
        var http = fixture.CreateClient();
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scopes.IdentityInternal };
        ClientAssertion.AddTo(form, IdentityServerFixture.InternalApiClientId, fixture.CreateClientAssertion(IdentityServerFixture.InternalApiClientId));
        var response = await new OidcTestClient(http).TokenAsync(form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, response.Body.ToString());
        http.DefaultRequestHeaders.Authorization = new("Bearer", response.AccessToken);
        return http;
    }

    [Fact]
    public async Task Provisioning_creates_a_confirmed_admin_and_emails_a_set_password_link()
    {
        using var http = await InternalClientAsync();
        var email = $"admin-{Guid.NewGuid():N}@it.local";

        using var response = await http.PostAsJsonAsync("/internal/tenant-admins",
            new { Email = email, DisplayName = "School Head", TenantName = "Northwood High", LoginUrl }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InternalTenantAdminResponse>(Ct);
        body!.Outcome.ShouldBe(TenantAdminOutcome.Created);
        body.UserId.ShouldNotBeNull();

        var user = await fixture.FindUserByEmailAsync(email);
        user.ShouldNotBeNull();
        // Confirmed at creation, otherwise ActiveUserConfirmation blocks every sign-in path and the membership
        // insert (IsActiveUserAsync) would reject the account too.
        user!.EmailConfirmed.ShouldBeTrue();
        user.DisplayName.ShouldBe("School Head");

        fixture.Emails.LastLinkFor(email, "provision-invite")
            .ShouldNotBeNullOrEmpty("the invitee must receive a link, since nobody knows the generated password");
    }

    [Fact]
    public async Task An_existing_account_is_reported_with_its_id_and_gets_no_set_password_link()
    {
        using var http = await InternalClientAsync();
        var existing = await fixture.CreateUserAsync("existing-admin");

        using var response = await http.PostAsJsonAsync("/internal/tenant-admins",
            new { Email = existing.Email, DisplayName = "Ignored", TenantName = "Riverside Academy", LoginUrl }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InternalTenantAdminResponse>(Ct);
        body!.Outcome.ShouldBe(TenantAdminOutcome.AlreadyExists);
        // Unlike self-registration, the id is returned: the caller needs it to attach the tenant membership.
        body.UserId.ShouldBe(existing.Id);

        fixture.Emails.LastLinkFor(existing.Email!, "provision-invite")
            .ShouldBeNull("someone who already has a password must not be sent a reset link");
        fixture.Emails.LastLinkFor(existing.Email!, "provision-granted").ShouldBe(LoginUrl);
    }

    [Fact]
    public async Task The_emailed_link_can_actually_set_a_password()
    {
        using var http = await InternalClientAsync();
        var email = $"settable-{Guid.NewGuid():N}@it.local";

        using var provision = await http.PostAsJsonAsync("/internal/tenant-admins",
            new { Email = email, DisplayName = "Settable Head", TenantName = "Lakeside School", LoginUrl }, Ct);
        provision.StatusCode.ShouldBe(HttpStatusCode.OK);

        var link = new Uri(fixture.Emails.LastLinkFor(email, "provision-invite").ShouldNotBeNull());
        var query = QueryHelpers.ParseQuery(link.Query);

        var browser = new OidcTestClient(fixture.CreateClient());
        var resetPage = new Uri(browser.Http.BaseAddress!, link.PathAndQuery);
        const string NewPassword = "Brand!New9Password";

        using var reset = await browser.Http.PostAsync(resetPage, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userId"] = query["userId"]!,
            ["code"] = query["code"]!,
            ["password"] = NewPassword,
            ["confirmPassword"] = NewPassword,
            ["__RequestVerificationToken"] = await browser.GetAntiforgeryTokenAsync(resetPage),
        }), Ct);

        (await reset.Content.ReadAsStringAsync(Ct)).ShouldContain("Your password has been reset");
    }

    [Fact]
    public async Task A_token_without_the_internal_scope_cannot_provision()
    {
        var http = fixture.CreateClient();
        var response = await new OidcTestClient(http).ClientCredentialsAsync(
            IdentityServerFixture.TenantServiceClientId, IdentityServerFixture.ServiceClientSecret, Scopes.ApiRead);
        http.DefaultRequestHeaders.Authorization = new("Bearer", response.AccessToken);

        using var provision = await http.PostAsJsonAsync("/internal/tenant-admins",
            new { Email = "nobody@it.local", DisplayName = "Nobody", TenantName = "Nowhere", LoginUrl }, Ct);

        provision.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await fixture.FindUserByEmailAsync("nobody@it.local")).ShouldBeNull();
    }
}
