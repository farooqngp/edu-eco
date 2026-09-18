using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EduEco.Api.Security.Authorization;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthorizationTests(ApiFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tenant_bound_service_client_reads_its_tenant()
    {
        var token = await fixture.GetServiceTokenAsync(ApiFixture.TenantServiceClientId, Scopes.ApiRead);
        using var client = fixture.CreateApiClient(token);

        var tenant = await client.GetFromJsonAsync<JsonElement>("api/v1/tenants/current", Ct);

        tenant.GetProperty("id").GetInt64().ShouldBe(fixture.TenantAlpha.Id);
    }

    [Fact]
    public async Task Service_client_without_tenant_is_refused_tenant_scoped_operations()
    {
        var token = await fixture.GetServiceTokenAsync(ApiFixture.PlatformServiceClientId, Scopes.ApiRead);
        using var client = fixture.CreateApiClient(token);

        using var response = await client.GetAsync("api/v1/tenants/current", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthenticationTests.ProblemCodeAsync(response)).ShouldBe(AuthorizationFailureCodes.TenantRequired);
    }

    [Fact]
    public async Task Service_clients_cannot_use_user_only_permissions()
    {
        var token = await fixture.GetServiceTokenAsync(ApiFixture.TenantServiceClientId, $"{Scopes.ApiRead} {Scopes.ApiWrite}");
        using var client = fixture.CreateApiClient(token);

        using var response = await client.GetAsync("api/v1/memberships", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthenticationTests.ProblemCodeAsync(response)).ShouldBe(AuthorizationFailureCodes.ServiceClientNotAllowed);
    }

    [Fact]
    public async Task Teacher_can_read_tenant_but_not_manage_users()
    {
        var teacher = await fixture.CreateUserAsync("teacher", [(fixture.TenantAlpha, Roles.Teacher)]);
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(teacher));

        (await client.GetAsync("api/v1/tenants/current", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await client.GetAsync("api/v1/memberships", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuthenticationTests.ProblemCodeAsync(response)).ShouldBe(AuthorizationFailureCodes.PermissionDenied);
    }

    [Fact]
    public async Task Forged_role_claims_are_ignored_permissions_come_from_the_database()
    {
        var student = await fixture.CreateUserAsync("forger", [(fixture.TenantAlpha, Roles.Student)]);
        var token = TestTokens.Mint(fixture.SigningCertificate,
            TestTokens.UserClaims(student.Id, fixture.TenantAlpha.Id, $"{Scopes.ApiRead} {Scopes.ApiWrite}", Roles.TenantAdmin, Roles.PlatformAdmin));
        using var client = fixture.CreateApiClient(token);

        (await client.GetAsync("api/v1/memberships", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("api/v1/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Missing_scope_returns_insufficient_scope_even_with_permission()
    {
        var admin = await fixture.CreateUserAsync("readonly-admin", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        var token = await fixture.GetUserTokenAsync(admin, clientId: ApiFixture.ReadOnlyWebClientId, scope: $"openid {Scopes.ApiRead}");
        using var client = fixture.CreateApiClient(token);

        using var response = await client.GetAsync("api/v1/memberships", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.ToString().ShouldBe($"Bearer error=\"insufficient_scope\", scope=\"{Scopes.ApiWrite}\"");
        (await AuthenticationTests.ProblemCodeAsync(response)).ShouldBe("insufficient_scope");
    }

    [Fact]
    public async Task Tenant_admin_sees_only_own_tenant_and_foreign_ids_are_not_found()
    {
        var adminAlpha = await fixture.CreateUserAsync("admin-alpha", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        var betaMember = await fixture.CreateUserAsync("beta-member", [(fixture.TenantBeta, Roles.Student)]);
        var adminBeta = await fixture.CreateUserAsync("admin-beta", [(fixture.TenantBeta, Roles.TenantAdmin)]);

        // Find a beta membership id through a beta admin.
        using var betaClient = fixture.CreateApiClient(await fixture.GetUserTokenAsync(adminBeta));
        var betaPage = await betaClient.GetFromJsonAsync<JsonElement>("api/v1/memberships?pageSize=200", Ct);
        var betaMembershipId = betaPage.GetProperty("items").EnumerateArray()
            .Single(m => m.GetProperty("userId").GetInt64() == betaMember.Id).GetProperty("id").GetInt64();

        using var alphaClient = fixture.CreateApiClient(await fixture.GetUserTokenAsync(adminAlpha));
        var alphaPage = await alphaClient.GetFromJsonAsync<JsonElement>("api/v1/memberships?pageSize=200", Ct);
        alphaPage.GetProperty("items").EnumerateArray().ShouldAllBe(m => m.GetProperty("tenantId").GetInt64() == fixture.TenantAlpha.Id);

        (await alphaClient.GetAsync($"api/v1/memberships/{betaMembershipId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alphaClient.DeleteAsync($"api/v1/memberships/{betaMembershipId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await betaClient.GetAsync($"api/v1/memberships/{betaMembershipId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Membership_creation_enforces_business_rules()
    {
        var admin = await fixture.CreateUserAsync("creator", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        var newcomer = await fixture.CreateUserAsync("newcomer");
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(admin));

        using var created = await client.PostAsJsonAsync("api/v1/memberships", new { userId = newcomer.Id, role = Roles.Teacher }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        created.Headers.Location!.ToString().ShouldContain("/api/v1/memberships/");
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("tenantId").GetInt64().ShouldBe(fixture.TenantAlpha.Id, "tenant comes from the token, never from input");

        (await client.PostAsJsonAsync("api/v1/memberships", new { userId = newcomer.Id, role = Roles.Teacher }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("api/v1/memberships", new { userId = newcomer.Id, role = Roles.PlatformAdmin }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("api/v1/memberships", new { userId = long.MaxValue, role = Roles.Student }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("api/v1/memberships", new { userId = 0, role = Roles.Student }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("api/v1/memberships?pageSize=100000", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var ownMembershipId = (await client.GetFromJsonAsync<JsonElement>("api/v1/memberships?pageSize=200", Ct))
            .GetProperty("items").EnumerateArray().Single(m => m.GetProperty("userId").GetInt64() == admin.Id).GetProperty("id").GetInt64();
        (await client.DeleteAsync($"api/v1/memberships/{ownMembershipId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Revoked_membership_takes_effect_on_next_request_despite_valid_token()
    {
        var adminA = await fixture.CreateUserAsync("revoker", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        var adminB = await fixture.CreateUserAsync("revokee", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        using var clientA = fixture.CreateApiClient(await fixture.GetUserTokenAsync(adminA));
        using var clientB = fixture.CreateApiClient(await fixture.GetUserTokenAsync(adminB));

        // Warm B's permission cache.
        var page = await clientB.GetFromJsonAsync<JsonElement>("api/v1/memberships?pageSize=200", Ct);
        var membershipB = page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("userId").GetInt64() == adminB.Id).GetProperty("id").GetInt64();

        (await clientA.DeleteAsync($"api/v1/memberships/{membershipB}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clientB.GetAsync("api/v1/memberships", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Platform_admin_lists_all_tenants_and_tenant_admin_cannot()
    {
        var platformAdmin = await fixture.CreateUserAsync("platform", memberships: null, Roles.PlatformAdmin);
        var tenantAdmin = await fixture.CreateUserAsync("tenant-admin", [(fixture.TenantAlpha, Roles.TenantAdmin)]);

        using var platformClient = fixture.CreateApiClient(await fixture.GetUserTokenAsync(platformAdmin, fixture.TenantBeta.Code));
        var tenants = await platformClient.GetFromJsonAsync<JsonElement>("api/v1/tenants?pageSize=200", Ct);
        var ids = tenants.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetInt64()).ToList();
        ids.ShouldContain(fixture.TenantAlpha.Id);
        ids.ShouldContain(fixture.TenantBeta.Id);

        using var tenantClient = fixture.CreateApiClient(await fixture.GetUserTokenAsync(tenantAdmin));
        (await tenantClient.GetAsync("api/v1/tenants", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void Every_endpoint_is_protected_unless_explicitly_anonymous()
    {
        var endpoints = fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.ShouldNotBeEmpty();

        var anonymous = endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null).Select(e => e.RoutePattern.RawText).ToList();
        anonymous.ShouldBe(["/health/live"], "only explicitly approved endpoints may be anonymous (OpenAPI/Scalar are Development-only)");

        foreach (var endpoint in endpoints.Where(e => e.RoutePattern.RawText!.StartsWith("api/", StringComparison.Ordinal)))
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty($"{endpoint.DisplayName} must declare [Authorize] or [HasPermission]");
        }
    }
}
