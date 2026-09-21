using System.Net;
using System.Net.Http.Json;
using EduEco.Core.Authorization;
using EduEco.Identity.Controllers;
using EduEco.Infrastructure.Security;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

/// <summary>
/// Server-to-server registration used by EduEco.Api's Angular-facing <c>/api/v1/auth/register</c>. Callers authenticate
/// with a client-credentials token scoped <c>identity.internal</c> (see <see cref="IdentityServerFixture.InternalApiClientId"/>).
/// </summary>
[Collection(IdentityServerCollection.Name)]
public sealed class InternalRegistrationTests(IdentityServerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> GetInternalApiTokenAsync(OidcTestClient client)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scopes.IdentityInternal };
        ClientAssertion.AddTo(form, IdentityServerFixture.InternalApiClientId, fixture.CreateClientAssertion(IdentityServerFixture.InternalApiClientId));
        var response = await client.TokenAsync(form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, response.Body.ToString());
        return response.AccessToken;
    }

    [Fact]
    public async Task Registration_creates_an_unconfirmed_user_and_sends_a_confirmation_email()
    {
        var http = fixture.CreateClient();
        var token = await GetInternalApiTokenAsync(new OidcTestClient(http));
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var email = $"newuser-{Guid.NewGuid():N}@it.local";

        using var response = await http.PostAsJsonAsync("/internal/registrations",
            new { Email = email, PhoneNumber = "+15551234567", DisplayName = "New User", Password = IdentityServerFixture.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InternalRegistrationResponse>(Ct);
        body!.Outcome.ShouldBe(RegistrationOutcome.Created);
        body.UserId.ShouldNotBeNull();

        var user = await fixture.FindUserByEmailAsync(email);
        user.ShouldNotBeNull();
        user!.EmailConfirmed.ShouldBeFalse();
        user.PhoneNumber.ShouldBe("+15551234567");
        fixture.Emails.LastLinkFor(email, "confirm").ShouldNotBeNull();
    }

    [Fact]
    public async Task Duplicate_email_reports_already_exists_without_sending_another_email()
    {
        var http = fixture.CreateClient();
        var token = await GetInternalApiTokenAsync(new OidcTestClient(http));
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var email = $"dup-{Guid.NewGuid():N}@it.local";

        await http.PostAsJsonAsync("/internal/registrations", new { Email = email, DisplayName = "First", Password = IdentityServerFixture.Password }, Ct);
        var before = fixture.Emails.Sent.Count;

        using var response = await http.PostAsJsonAsync("/internal/registrations", new { Email = email, DisplayName = "Second", Password = IdentityServerFixture.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InternalRegistrationResponse>(Ct);
        body!.Outcome.ShouldBe(RegistrationOutcome.AlreadyExists);
        body.UserId.ShouldBeNull();
        fixture.Emails.Sent.Count.ShouldBe(before, "a duplicate registration must not send a second confirmation email");
    }

    [Fact]
    public async Task Weak_password_is_reported_as_validation_failed()
    {
        var http = fixture.CreateClient();
        var token = await GetInternalApiTokenAsync(new OidcTestClient(http));
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var email = $"weak-{Guid.NewGuid():N}@it.local";

        // 12+ chars (passes the [StringLength] annotation / model binding) but lacks the required complexity, so it
        // reaches UserManager.CreateAsync and surfaces as a genuine ValidationFailed result, not an automatic 400.
        using var response = await http.PostAsJsonAsync("/internal/registrations", new { Email = email, DisplayName = "Weak", Password = "aaaaaaaaaaaa" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InternalRegistrationResponse>(Ct);
        body!.Outcome.ShouldBe(RegistrationOutcome.ValidationFailed);
        body.Errors.ShouldNotBeEmpty();
        (await fixture.FindUserByEmailAsync(email)).ShouldBeNull();
    }

    [Fact]
    public async Task A_normal_resource_server_token_cannot_call_the_internal_endpoint()
    {
        // Realistic threat model: a token meant for EduEco.Api (wrong audience, no identity.internal scope) must not work here.
        var http = fixture.CreateClient();
        var client = new OidcTestClient(http);
        var response = await client.ClientCredentialsAsync(IdentityServerFixture.TenantServiceClientId, IdentityServerFixture.ServiceClientSecret, Scopes.ApiRead);
        http.DefaultRequestHeaders.Authorization = new("Bearer", response.AccessToken);

        using var registration = await http.PostAsJsonAsync("/internal/registrations",
            new { Email = "irrelevant@it.local", DisplayName = "X", Password = IdentityServerFixture.Password }, Ct);

        registration.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
