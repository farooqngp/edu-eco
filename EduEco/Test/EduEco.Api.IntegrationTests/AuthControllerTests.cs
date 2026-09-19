using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Core.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthControllerTests(ApiFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_with_a_valid_invite_creates_an_unconfirmed_user_with_a_membership()
    {
        var code = await IssueInviteAsync(Roles.Student);
        var email = $"reg-{Guid.NewGuid():N}@it.local";
        using var client = fixture.CreateApiClient();

        using var response = await client.PostAsJsonAsync("api/v1/auth/register",
            new { InviteCode = code, Email = email, DisplayName = "New Student", Password = ApiFixture.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("requiresEmailConfirmation").GetBoolean().ShouldBeTrue();

        var user = await fixture.FindUserByEmailAsync(email);
        user.ShouldNotBeNull();
        user!.EmailConfirmed.ShouldBeFalse();

        await using var scope = fixture.Identity.Services.CreateAsyncScope();
        var tenantId = await scope.ServiceProvider.GetRequiredService<IQueryExecutor>().QuerySingleOrDefaultAsync<long?>(
            "SELECT TenantId FROM auth.UserTenantMemberships WHERE UserId = @UserId", new { UserId = user.Id }, Ct);
        tenantId.ShouldBe(fixture.TenantAlpha.Id);
    }

    [Fact]
    public async Task Register_with_an_unknown_invite_code_is_rejected()
    {
        using var client = fixture.CreateApiClient();

        using var response = await client.PostAsJsonAsync("api/v1/auth/register",
            new { InviteCode = "not-a-real-code", Email = $"x-{Guid.NewGuid():N}@it.local", DisplayName = "Unknown", Password = ApiFixture.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_single_use_invite_cannot_register_a_second_account()
    {
        var code = await IssueInviteAsync(Roles.Student);
        using var client = fixture.CreateApiClient();

        using var first = await client.PostAsJsonAsync("api/v1/auth/register",
            new { InviteCode = code, Email = $"first-{Guid.NewGuid():N}@it.local", DisplayName = "First", Password = ApiFixture.Password }, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        using var second = await client.PostAsJsonAsync("api/v1/auth/register",
            new { InviteCode = code, Email = $"second-{Guid.NewGuid():N}@it.local", DisplayName = "Second", Password = ApiFixture.Password }, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Login_before_email_confirmation_is_rejected()
    {
        var code = await IssueInviteAsync(Roles.Student);
        var email = $"unconfirmed-{Guid.NewGuid():N}@it.local";
        using var client = fixture.CreateApiClient();
        await client.PostAsJsonAsync("api/v1/auth/register", new { InviteCode = code, Email = email, DisplayName = "Unconfirmed", Password = ApiFixture.Password }, Ct);

        using var response = await client.PostAsJsonAsync("api/v1/auth/login", new { Identifier = email, Password = ApiFixture.Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_after_confirmation_issues_tokens_and_refresh_works()
    {
        var code = await IssueInviteAsync(Roles.Teacher);
        var email = $"confirmed-{Guid.NewGuid():N}@it.local";
        using var client = fixture.CreateApiClient();
        await client.PostAsJsonAsync("api/v1/auth/register", new { InviteCode = code, Email = email, DisplayName = "Confirmed", Password = ApiFixture.Password }, Ct);
        await fixture.ConfirmEmailAsync(email);

        using var login = await client.PostAsJsonAsync("api/v1/auth/login", new { Identifier = email, Password = ApiFixture.Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(Ct));
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>(Ct);
        tokens.GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
        var refreshToken = tokens.GetProperty("refreshToken").GetString();
        refreshToken.ShouldNotBeNullOrEmpty();

        using var refreshed = await client.PostAsJsonAsync("api/v1/auth/refresh", new { RefreshToken = refreshToken }, Ct);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync(Ct));
        (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        var code = await IssueInviteAsync(Roles.Student);
        var email = $"wrongpw-{Guid.NewGuid():N}@it.local";
        using var client = fixture.CreateApiClient();
        await client.PostAsJsonAsync("api/v1/auth/register", new { InviteCode = code, Email = email, DisplayName = "WrongPw", Password = ApiFixture.Password }, Ct);
        await fixture.ConfirmEmailAsync(email);

        using var response = await client.PostAsJsonAsync("api/v1/auth/login", new { Identifier = email, Password = "Wrong!Password9" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Forgot_password_always_returns_accepted()
    {
        var code = await IssueInviteAsync(Roles.Student);
        var email = $"knownreset-{Guid.NewGuid():N}@it.local";
        using var client = fixture.CreateApiClient();
        await client.PostAsJsonAsync("api/v1/auth/register", new { InviteCode = code, Email = email, DisplayName = "KnownReset", Password = ApiFixture.Password }, Ct);
        await fixture.ConfirmEmailAsync(email);

        using var known = await client.PostAsJsonAsync("api/v1/auth/forgot-password", new { Email = email }, Ct);
        using var unknown = await client.PostAsJsonAsync("api/v1/auth/forgot-password", new { Email = $"nobody-{Guid.NewGuid():N}@it.local" }, Ct);

        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private async Task<string> IssueInviteAsync(string roleName)
    {
        var admin = await fixture.CreateUserAsync($"auth-admin-{Guid.NewGuid():N}", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        using var client = fixture.CreateApiClient(await fixture.GetUserTokenAsync(admin));

        using var response = await client.PostAsJsonAsync("api/v1/invites", new { RoleName = roleName, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7) }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString()!;
    }
}
