using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace EduEco.Identity.IntegrationTests;

[Collection(IdentityServerCollection.Name)]
public sealed class AuthorizationCodeFlowTests(IdentityServerFixture fixture)
{
    [Fact]
    public async Task Code_flow_with_pkce_issues_tenant_scoped_tokens()
    {
        var teacher = await fixture.CreateUserAsync("teacher", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());

        var tokens = await client.SignInAsync(teacher.Email!);

        tokens.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.Body.ToString());
        tokens.Get("id_token").ShouldNotBeNullOrEmpty();
        tokens.Get("refresh_token").ShouldNotBeNullOrEmpty();

        var jwt = await client.ValidateAccessTokenAsync(tokens.AccessToken);
        jwt.Subject.ShouldBe(teacher.Id.ToString(CultureInfo.InvariantCulture));
        jwt.GetClaim(EduEcoClaimTypes.TenantId).Value.ShouldBe(fixture.TenantAlpha.Id.ToString(CultureInfo.InvariantCulture));
        jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).ShouldBe([Roles.Teacher]);
        jwt.GetClaim("client_id").Value.ShouldBe(IdentityServerFixture.WebClientId);

        // Private claims and PII never reach the access token.
        jwt.TryGetClaim("email", out _).ShouldBeFalse();
        jwt.TryGetClaim("eduEco_sstamp", out _).ShouldBeFalse();
        jwt.TryGetClaim("eduEco_session_start", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Authorization_request_without_pkce_is_rejected()
    {
        var teacher = await fixture.CreateUserAsync("nopkce", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());

        var (response, redirect) = await client.FollowAsync(OidcTestClient.AuthorizeUrl(codeChallenge: null), teacher.Email!);

        ErrorOf(response, redirect).ShouldBe("invalid_request");
    }

    [Fact]
    public async Task Plain_pkce_method_is_rejected()
    {
        var teacher = await fixture.CreateUserAsync("plainpkce", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());

        var (response, redirect) = await client.FollowAsync(
            OidcTestClient.AuthorizeUrl("plain-challenge-value-plain-challenge-value-123", codeChallengeMethod: "plain"), teacher.Email!);

        ErrorOf(response, redirect).ShouldBe("invalid_request");
    }

    [Fact]
    public async Task Code_cannot_be_redeemed_with_wrong_verifier()
    {
        var teacher = await fixture.CreateUserAsync("verifier", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var (_, challenge) = OidcTestClient.CreatePkce();

        var (_, redirect) = await client.FollowAsync(OidcTestClient.AuthorizeUrl(challenge), teacher.Email!);
        var code = QueryHelpers.ParseQuery(redirect!.Query)["code"].ToString();

        var tokens = await client.RedeemCodeAsync(code, OidcTestClient.CreatePkce().Verifier);

        tokens.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tokens.Error.ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task User_without_tenant_access_is_denied()
    {
        var outsider = await fixture.CreateUserAsync("outsider");
        var client = new OidcTestClient(fixture.CreateClient());

        var (response, redirect) = await client.FollowAsync(OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge), outsider.Email!);

        ErrorOf(response, redirect).ShouldBe("access_denied");
    }

    [Fact]
    public async Task Multi_tenant_user_must_choose_and_cannot_pick_foreign_tenant()
    {
        var multi = await fixture.CreateUserAsync("multi", [(fixture.TenantAlpha, Roles.Teacher), (fixture.TenantBeta, Roles.Student)]);
        var outsiderTenantUser = await fixture.CreateUserAsync("alphaonly", [(fixture.TenantAlpha, Roles.Student)]);

        // No tenant parameter → tenant picker.
        var picker = new OidcTestClient(fixture.CreateClient());
        var (response, redirect) = await picker.FollowAsync(OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge), multi.Email!);
        redirect.ShouldBeNull();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(fixture.TenantBeta.Code);

        // Explicit tenant → token for that tenant with that tenant's roles.
        var chooser = new OidcTestClient(fixture.CreateClient());
        var tokens = await chooser.SignInAsync(multi.Email!, tenant: fixture.TenantBeta.Code);
        var jwt = await chooser.ValidateAccessTokenAsync(tokens.AccessToken);
        jwt.GetClaim(EduEcoClaimTypes.TenantId).Value.ShouldBe(fixture.TenantBeta.Id.ToString(CultureInfo.InvariantCulture));
        jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).ShouldBe([Roles.Student]);

        // Tenant the user does not belong to → access_denied.
        var intruder = new OidcTestClient(fixture.CreateClient());
        var (denied, deniedRedirect) = await intruder.FollowAsync(
            OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge, tenant: fixture.TenantBeta.Code), outsiderTenantUser.Email!);
        ErrorOf(denied, deniedRedirect).ShouldBe("access_denied");
    }

    [Fact]
    public async Task Platform_admin_can_act_for_any_tenant()
    {
        var admin = await fixture.CreateUserAsync("admin", memberships: null, Roles.PlatformAdmin);
        var client = new OidcTestClient(fixture.CreateClient());

        var tokens = await client.SignInAsync(admin.Email!, tenant: fixture.TenantBeta.Code);

        var jwt = await client.ValidateAccessTokenAsync(tokens.AccessToken);
        jwt.GetClaim(EduEcoClaimTypes.TenantId).Value.ShouldBe(fixture.TenantBeta.Id.ToString(CultureInfo.InvariantCulture));
        jwt.Claims.Where(c => c.Type == "role").Select(c => c.Value).ShouldContain(Roles.PlatformAdmin);
    }

    [Fact]
    public async Task Refresh_token_rotates_and_reuse_revokes_the_chain()
    {
        var teacher = await fixture.CreateUserAsync("rotate", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var initial = await client.SignInAsync(teacher.Email!);

        var rotated = await client.RefreshAsync(initial.RefreshToken);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK, rotated.Body.ToString());
        rotated.RefreshToken.ShouldNotBe(initial.RefreshToken);

        var replay = await client.RefreshAsync(initial.RefreshToken);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        replay.Error.ShouldBe("invalid_grant");

        // Reuse detection revokes the whole token chain, including the legitimately rotated token.
        var afterReplay = await client.RefreshAsync(rotated.RefreshToken);
        afterReplay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        afterReplay.Error.ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Refresh_is_refused_after_account_deactivation()
    {
        var teacher = await fixture.CreateUserAsync("deactivated", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var tokens = await client.SignInAsync(teacher.Email!);

        await fixture.UpdateUserAsync(teacher.Id, u => u.IsActive = false);

        var refreshed = await client.RefreshAsync(tokens.RefreshToken);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refreshed.Error.ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Refresh_is_refused_after_security_stamp_change()
    {
        var teacher = await fixture.CreateUserAsync("stamp", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var tokens = await client.SignInAsync(teacher.Email!);

        await fixture.UpdateUserAsync(teacher.Id, u => u.SecurityStamp = Guid.NewGuid().ToString("N"));

        var refreshed = await client.RefreshAsync(tokens.RefreshToken);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refreshed.Error.ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Userinfo_returns_subject_tenant_and_scoped_profile()
    {
        var teacher = await fixture.CreateUserAsync("userinfo", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var tokens = await client.SignInAsync(teacher.Email!);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await client.Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("sub").GetString().ShouldBe(teacher.Id.ToString(CultureInfo.InvariantCulture));
        json.RootElement.GetProperty(EduEcoClaimTypes.TenantId).GetString().ShouldBe(fixture.TenantAlpha.Id.ToString(CultureInfo.InvariantCulture));
        json.RootElement.GetProperty("email").GetString().ShouldBe(teacher.Email);
    }

    [Fact]
    public async Task Prompt_none_without_session_returns_login_required()
    {
        using var http = fixture.CreateClient();

        using var response = await http.GetAsync(OidcTestClient.AuthorizeUrl(OidcTestClient.CreatePkce().Challenge) + "&prompt=none", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString().ShouldBe("login_required");
    }

    private static string? ErrorOf(HttpResponseMessage response, Uri? clientRedirect)
    {
        if (clientRedirect is not null)
        {
            return QueryHelpers.ParseQuery(clientRedirect.Query).TryGetValue("error", out var error) ? error.ToString() : null;
        }

        // Errors that cannot be safely redirected (e.g. invalid redirect_uri) are rendered by the server.
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return body.Contains("invalid_request", StringComparison.Ordinal) ? "invalid_request" : $"{(int)response.StatusCode}: {body}";
    }
}
