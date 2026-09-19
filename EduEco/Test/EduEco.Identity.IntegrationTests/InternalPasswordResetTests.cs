using System.Net;
using System.Net.Http.Json;
using EduEco.Core.Authorization;
using EduEco.Infrastructure.Security;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

/// <summary>Server-to-server password-reset request used by EduEco.Api's <c>/api/v1/auth/forgot-password</c>.</summary>
[Collection(IdentityServerCollection.Name)]
public sealed class InternalPasswordResetTests(IdentityServerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> CreateAuthorizedClientAsync()
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
    public async Task Known_confirmed_account_receives_a_reset_link()
    {
        var http = await CreateAuthorizedClientAsync();
        var user = await fixture.CreateUserAsync("reset-known");

        using var response = await http.PostAsJsonAsync("/internal/password-resets", new { Email = user.Email }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.Emails.LastLinkFor(user.Email!, "reset").ShouldNotBeNull();
    }

    [Fact]
    public async Task Unknown_email_gets_the_identical_response_with_no_email_sent()
    {
        var http = await CreateAuthorizedClientAsync();
        var email = $"unknown-{Guid.NewGuid():N}@it.local";
        var before = fixture.Emails.Sent.Count;

        using var response = await http.PostAsJsonAsync("/internal/password-resets", new { Email = email }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        fixture.Emails.Sent.Count.ShouldBe(before, "no enumeration: an unknown address must not trigger an email");
    }
}
