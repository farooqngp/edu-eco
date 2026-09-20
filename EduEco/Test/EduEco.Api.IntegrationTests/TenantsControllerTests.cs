using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

/// <summary>
/// Tenant provisioning: creating the tenant, attaching its administrator, and refusing everyone else. The account
/// itself is created in the Identity host, which <see cref="FakeTenantAdminProvisioner"/> stands in for here.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantsControllerTests(ApiFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object NewTenantPayload(string code, string? adminEmail = null) => new
    {
        Code = code,
        Name = $"{code} School",
        AdminEmail = adminEmail ?? $"{code}-admin@example.com",
        AdminDisplayName = "School Head",
    };

    private async Task<HttpClient> PlatformAdminClientAsync()
    {
        var admin = await fixture.CreateUserAsync($"platform-{Guid.NewGuid():N}", memberships: null, Roles.PlatformAdmin);
        // A platform admin may act for any tenant; tenants.manage is TenantScoped:false, so the tenant is irrelevant
        // to the permission itself — this just gets a real, introspectable token through the standard flow.
        return fixture.CreateApiClient(await fixture.GetUserTokenAsync(admin, fixture.TenantAlpha.Code));
    }

    /// <summary>The provisioned account must really exist — the membership has a foreign key to it.</summary>
    private async Task<string> ArrangeProvisionedAdminAsync(string label)
    {
        var user = await fixture.CreateUserAsync($"{label}-{Guid.NewGuid():N}");
        fixture.Provisioner.NextUserId = user.Id;
        return user.Email!;
    }

    [Fact]
    public async Task Platform_admin_provisions_a_tenant_and_its_administrator()
    {
        fixture.Provisioner.Reset();
        var adminEmail = await ArrangeProvisionedAdminAsync("provisioned");
        using var client = await PlatformAdminClientAsync();
        var code = $"alpha-{Guid.NewGuid():N}"[..20];

        using var response = await client.PostAsJsonAsync("api/v1/tenants", NewTenantPayload(code, adminEmail), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe(code);
        body.GetProperty("adminUserId").GetInt64().ShouldBeGreaterThan(0);
        body.GetProperty("invitationSent").GetBoolean().ShouldBeTrue();
        // The response must never carry a credential; the admin sets their own password from the emailed link.
        body.TryGetProperty("password", out _).ShouldBeFalse();

        fixture.Provisioner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_duplicate_code_is_rejected_before_any_account_is_created()
    {
        fixture.Provisioner.Reset();
        var adminEmail = await ArrangeProvisionedAdminAsync("dup");
        using var client = await PlatformAdminClientAsync();
        var code = $"dup-{Guid.NewGuid():N}"[..18];

        using var first = await client.PostAsJsonAsync("api/v1/tenants", NewTenantPayload(code, adminEmail), Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var callsAfterFirst = fixture.Provisioner.Calls.Count;

        using var second = await client.PostAsJsonAsync("api/v1/tenants", NewTenantPayload(code, "someone-else@example.com"), Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // The pre-check matters: provisioning an account for a request that cannot succeed would strand it with no
        // tenant, and account creation in Identity cannot be rolled back.
        fixture.Provisioner.Calls.Count.ShouldBe(callsAfterFirst, "a duplicate code must not reach the Identity host");
    }

    [Fact]
    public async Task An_existing_account_is_reused_as_the_administrator()
    {
        fixture.Provisioner.Reset();
        var existing = await fixture.CreateUserAsync($"existing-{Guid.NewGuid():N}", [(fixture.TenantBeta, Roles.Teacher)]);
        fixture.Provisioner.NextOutcome = AdminProvisionOutcome.AlreadyExists;
        fixture.Provisioner.NextUserId = existing.Id;

        using var client = await PlatformAdminClientAsync();
        using var response = await client.PostAsJsonAsync("api/v1/tenants", NewTenantPayload($"reuse-{Guid.NewGuid():N}"[..20], existing.Email), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("adminUserId").GetInt64().ShouldBe(existing.Id);
        // Nothing was sent to someone who already has a password.
        body.GetProperty("invitationSent").GetBoolean().ShouldBeFalse();

        fixture.Provisioner.Reset();
    }

    [Fact]
    public async Task Provisioning_is_replay_safe()
    {
        fixture.Provisioner.Reset();
        var admin = await fixture.CreateUserAsync($"replay-{Guid.NewGuid():N}", [(fixture.TenantBeta, Roles.Student)]);
        fixture.Provisioner.NextUserId = admin.Id;

        using var client = await PlatformAdminClientAsync();
        var payload = NewTenantPayload($"replay-{Guid.NewGuid():N}"[..20], admin.Email);

        using var first = await client.PostAsJsonAsync("api/v1/tenants", payload, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Re-running the same request after a partial failure must converge, not dead-end on the membership's
        // unique constraint. The tenant code already exists now, so this is the Conflict path, not a 500.
        using var replay = await client.PostAsJsonAsync("api/v1/tenants", payload, Ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        fixture.Provisioner.Reset();
    }

    [Fact]
    public async Task A_tenant_admin_cannot_provision_tenants()
    {
        var tenantAdmin = await fixture.CreateUserAsync($"ta-{Guid.NewGuid():N}", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(tenantAdmin));

        using var response = await client.PostAsJsonAsync("api/v1/tenants", NewTenantPayload($"nope-{Guid.NewGuid():N}"[..18]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_rejected()
    {
        using var client = fixture.CreateApiClient();

        using var response = await client.PostAsJsonAsync("api/v1/tenants", NewTenantPayload("anon-school"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_invalid_payload_is_rejected()
    {
        using var client = await PlatformAdminClientAsync();

        using var response = await client.PostAsJsonAsync(
            "api/v1/tenants",
            new { Code = "x", Name = "", AdminEmail = "not-an-email", AdminDisplayName = "A" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
