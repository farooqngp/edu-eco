using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests(ApiFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_token_returns_401_problem_with_bearer_challenge()
    {
        using var client = fixture.CreateApiClient();

        using var response = await client.GetAsync("api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Bearer");
        (await ProblemCodeAsync(response)).ShouldBe("token_required");
    }

    [Fact]
    public async Task Real_service_token_from_identity_server_is_accepted()
    {
        var token = await fixture.GetServiceTokenAsync(ApiFixture.TenantServiceClientId, $"{Scopes.ApiRead} {Scopes.ApiWrite}");
        using var client = fixture.CreateApiClient(token);

        var me = await client.GetFromJsonAsync<JsonElement>("api/v1/me", Ct);

        me.GetProperty("isServiceClient").GetBoolean().ShouldBeTrue();
        me.GetProperty("tenantId").GetInt64().ShouldBe(fixture.TenantAlpha.Id);
        var permissions = me.GetProperty("effectivePermissions").EnumerateArray().Select(p => p.GetString()).ToList();
        permissions.ShouldContain(Permissions.Tenants.Read);
        permissions.ShouldContain(Permissions.Courses.Write);
        permissions.ShouldNotContain(Permissions.Users.Manage);
    }

    [Fact]
    public async Task Minted_token_with_identity_signing_key_is_accepted_control()
    {
        // Control case: proves the negative tests below fail for the intended reason only.
        var token = TestTokens.Mint(fixture.SigningCertificate, TestTokens.ServiceClaims("minted", Scopes.ApiRead, fixture.TenantAlpha.Id));
        using var client = fixture.CreateApiClient(token);

        using var response = await client.GetAsync("api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static TheoryData<string> InvalidTokenCases => ["expired", "wrong-audience", "id-token-type", "wrong-issuer", "foreign-key", "unsigned"];

    [Theory]
    [MemberData(nameof(InvalidTokenCases))]
    public async Task Invalid_tokens_are_rejected_with_invalid_token(string scenario)
    {
        var claims = TestTokens.ServiceClaims("minted", Scopes.ApiRead, fixture.TenantAlpha.Id);
        var certificate = fixture.SigningCertificate;
        var token = scenario switch
        {
            "expired" => TestTokens.Mint(certificate, claims, expires: DateTime.UtcNow.AddMinutes(-5)),
            "wrong-audience" => TestTokens.Mint(certificate, claims, audience: "another-api"),
            "id-token-type" => TestTokens.Mint(certificate, claims, tokenType: "JWT"),
            "wrong-issuer" => TestTokens.Mint(certificate, claims, issuer: "https://evil.test/"),
            "foreign-key" => TestTokens.Mint(certificate, claims, signingCredentials: TestTokens.ForeignKey()),
            "unsigned" => string.Join('.', TestTokens.Mint(certificate, claims).Split('.')[..2]) + ".",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using var client = fixture.CreateApiClient(token);
        using var response = await client.GetAsync("api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, scenario);
        var challenge = response.Headers.WwwAuthenticate.Single();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldBe("error=\"invalid_token\"", "error details are not disclosed outside Development");
        (await ProblemCodeAsync(response)).ShouldBe("invalid_token");
    }

    [Fact]
    public async Task Tokens_for_deactivated_tenants_are_refused()
    {
        var tenant = await fixture.CreateTenantAsync("inactive");
        var token = TestTokens.Mint(fixture.SigningCertificate, TestTokens.ServiceClaims("minted", Scopes.ApiRead, tenant.Id));
        using var client = fixture.CreateApiClient(token);

        (await client.GetAsync("api/v1/tenants/current", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await fixture.SetTenantActiveAsync(tenant.Id, active: false);

        using var response = await client.GetAsync("api/v1/tenants/current", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response)).ShouldBe("tenant_inactive");
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        using var client = fixture.CreateApiClient();
        (await client.GetAsync("health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    internal static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
