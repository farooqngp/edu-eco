using System.Globalization;
using System.Net;
using System.Text.Json;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

[Collection(IdentityServerCollection.Name)]
public sealed class DiscoveryAndClientCredentialsTests(IdentityServerFixture fixture)
{
    [Fact]
    public async Task Discovery_document_advertises_only_oauth21_compliant_capabilities()
    {
        using var http = fixture.CreateClient();
        using var document = JsonDocument.Parse(await http.GetStringAsync("/.well-known/openid-configuration", TestContext.Current.CancellationToken));
        var root = document.RootElement;

        root.GetProperty("issuer").GetString().ShouldBe(IdentityServerFixture.Issuer);
        Strings(root, "grant_types_supported").ShouldBe(
            ["authorization_code", "client_credentials", "refresh_token", "urn:ietf:params:oauth:grant-type:token-exchange"], ignoreOrder: true);
        Strings(root, "response_types_supported").ShouldBe(["code"]);
        Strings(root, "code_challenge_methods_supported").ShouldBe(["S256"]);
        Strings(root, "scopes_supported").ShouldContain(Scopes.ApiRead);
        root.GetProperty("jwks_uri").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("end_session_endpoint").GetString().ShouldEndWith("/connect/endsession");
        root.GetProperty("revocation_endpoint").GetString().ShouldEndWith("/connect/revoke");
        root.GetProperty("introspection_endpoint").GetString().ShouldEndWith("/connect/introspect");
    }

    [Fact]
    public async Task Tenant_bound_service_client_gets_rfc9068_access_token_with_tenant_and_audience()
    {
        var client = new OidcTestClient(fixture.CreateClient());

        var response = await client.ClientCredentialsAsync(IdentityServerFixture.TenantServiceClientId, IdentityServerFixture.ServiceClientSecret, "api.read api.sync");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, response.Body.ToString());
        response.Get("token_type").ShouldBe("Bearer");
        response.Body.GetProperty("expires_in").GetInt32().ShouldBeLessThanOrEqualTo(600);
        response.Get("refresh_token").ShouldBeNull("client_credentials must not issue refresh tokens");

        var jwt = await client.ValidateAccessTokenAsync(response.AccessToken);
        jwt.Typ.ShouldBe("at+jwt");
        jwt.Subject.ShouldBe(IdentityServerFixture.TenantServiceClientId);
        jwt.GetClaim("client_id").Value.ShouldBe(IdentityServerFixture.TenantServiceClientId);
        jwt.GetClaim(EduEcoClaimTypes.TenantId).Value.ShouldBe(fixture.TenantAlpha.Id.ToString(CultureInfo.InvariantCulture));
        jwt.Audiences.ShouldBe([Resources.Api]);
        jwt.GetClaim("scope").Value.Split(' ').ShouldBe([Scopes.ApiRead, Scopes.ApiSync], ignoreOrder: true);
    }

    [Fact]
    public async Task Platform_service_client_token_has_no_tenant()
    {
        var client = new OidcTestClient(fixture.CreateClient());

        var response = await client.ClientCredentialsAsync(IdentityServerFixture.PlatformServiceClientId, IdentityServerFixture.ServiceClientSecret, Scopes.ApiSync);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, response.Body.ToString());
        var jwt = await client.ValidateAccessTokenAsync(response.AccessToken);
        jwt.TryGetClaim(EduEcoClaimTypes.TenantId, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Wrong_client_secret_is_rejected()
    {
        var client = new OidcTestClient(fixture.CreateClient());

        var response = await client.ClientCredentialsAsync(IdentityServerFixture.TenantServiceClientId, "wrong-secret", Scopes.ApiRead);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Error.ShouldBe("invalid_client");
    }

    [Fact]
    public async Task Scope_not_granted_to_client_is_rejected()
    {
        var client = new OidcTestClient(fixture.CreateClient());

        var response = await client.ClientCredentialsAsync(IdentityServerFixture.PlatformServiceClientId, IdentityServerFixture.ServiceClientSecret, Scopes.ApiWrite);

        // OpenIddict reports missing scope permissions as invalid_request (RFC 6749 §5.2 allows either for this case).
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Error.ShouldBeOneOf("invalid_scope", "invalid_request");
        response.Get("access_token").ShouldBeNull();
    }

    [Fact]
    public async Task Password_grant_is_not_supported()
    {
        var client = new OidcTestClient(fixture.CreateClient());

        var response = await client.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "someone@it.local",
            ["password"] = IdentityServerFixture.Password,
            ["client_id"] = IdentityServerFixture.WebClientId,
            ["client_secret"] = IdentityServerFixture.WebClientSecret,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Error.ShouldBe("unsupported_grant_type");
    }

    private static string[] Strings(JsonElement root, string property) =>
        [.. root.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];
}
