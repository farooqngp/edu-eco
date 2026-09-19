using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EduEco.Core.Authorization;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ProfileControllerTests(ApiFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        using var client = fixture.CreateApiClient();

        (await client.GetAsync("api/v1/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_before_any_update_is_not_found()
    {
        var user = await fixture.CreateUserAsync("profile-empty", [(fixture.TenantAlpha, Roles.Student)]);
        var token = await fixture.GetUserTokenAsync(user, fixture.TenantAlpha.Code);
        using var client = fixture.CreateApiClient(token);

        (await client.GetAsync("api/v1/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_then_get_round_trips()
    {
        var user = await fixture.CreateUserAsync("profile-rw", [(fixture.TenantAlpha, Roles.Student)]);
        var token = await fixture.GetUserTokenAsync(user, fixture.TenantAlpha.Code);
        using var client = fixture.CreateApiClient(token);

        using var updated = await client.PutAsJsonAsync("api/v1/profile",
            new { DateOfBirth = "2000-01-15", Address = "1 Main St", City = "Springfield", PostalCode = "12345", Country = "US" }, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fetched = await client.GetFromJsonAsync<JsonElement>("api/v1/profile", Ct);
        fetched.GetProperty("dateOfBirth").GetString().ShouldBe("2000-01-15");
        fetched.GetProperty("address").GetString().ShouldBe("1 Main St");
        fetched.GetProperty("city").GetString().ShouldBe("Springfield");

        // Updating again overwrites rather than creating a second row.
        using var overwritten = await client.PutAsJsonAsync("api/v1/profile",
            new { DateOfBirth = "2000-01-15", Address = "2 Other St", City = "Springfield", PostalCode = "12345", Country = "US" }, Ct);
        overwritten.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<JsonElement>("api/v1/profile", Ct)).GetProperty("address").GetString().ShouldBe("2 Other St");
    }

    [Fact]
    public async Task A_users_profile_is_never_visible_to_another_user()
    {
        var userA = await fixture.CreateUserAsync("profile-a", [(fixture.TenantAlpha, Roles.Student)]);
        var userB = await fixture.CreateUserAsync("profile-b", [(fixture.TenantAlpha, Roles.Student)]);
        using var clientA = fixture.CreateApiClient(await fixture.GetUserTokenAsync(userA, fixture.TenantAlpha.Code));
        using var clientB = fixture.CreateApiClient(await fixture.GetUserTokenAsync(userB, fixture.TenantAlpha.Code));

        await clientA.PutAsJsonAsync("api/v1/profile", new { Address = "A's address" }, Ct);

        (await clientB.GetAsync("api/v1/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Service_client_token_has_no_profile()
    {
        var token = await fixture.GetServiceTokenAsync(ApiFixture.TenantServiceClientId, Scopes.ApiRead);
        using var client = fixture.CreateApiClient(token);

        (await client.GetAsync("api/v1/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
