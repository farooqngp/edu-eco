using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class InvitesControllerTests(ApiFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tenant_admin_issues_an_invite()
    {
        var admin = await fixture.CreateUserAsync("invite-admin", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(admin));

        using var response = await client.PostAsJsonAsync("api/v1/invites", new { RoleName = Roles.Student, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("inviteId").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Non_admin_cannot_issue_invites()
    {
        var student = await fixture.CreateUserAsync("invite-student", [(fixture.TenantAlpha, Roles.Student)]);
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(student));

        using var response = await client.PostAsJsonAsync("api/v1/invites", new { RoleName = Roles.Student, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        using var client = fixture.CreateApiClient();

        using var response = await client.PostAsJsonAsync("api/v1/invites", new { RoleName = Roles.Student, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Non_assignable_role_is_rejected()
    {
        var admin = await fixture.CreateUserAsync("invite-admin2", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(admin));

        using var response = await client.PostAsJsonAsync("api/v1/invites", new { RoleName = Roles.PlatformAdmin, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
