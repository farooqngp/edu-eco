using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EduEco.Core.Authorization;
using EduEco.Infrastructure.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using static EduEco.Identity.IntegrationTests.Phase5Helpers;

namespace EduEco.Identity.IntegrationTests;

/// <summary>private_key_jwt (RFC 7523), PAR (RFC 9126), DPoP (RFC 9449), token exchange (RFC 8693), introspection (RFC 7662).</summary>
[Collection(IdentityServerCollection.Name)]
public sealed class AdvancedOAuthTests(IdentityServerFixture fixture)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- Discovery ----------

    [Fact]
    public async Task Discovery_advertises_par_dpop_and_backchannel_logout()
    {
        using var http = fixture.CreateClient();
        using var json = JsonDocument.Parse(await http.GetStringAsync("/.well-known/openid-configuration", Ct));
        var root = json.RootElement;

        root.GetProperty("pushed_authorization_request_endpoint").GetString().ShouldEndWith("/connect/par");
        root.GetProperty("dpop_signing_alg_values_supported").EnumerateArray().Select(e => e.GetString()).ShouldContain("ES256");
        root.GetProperty("backchannel_logout_supported").GetBoolean().ShouldBeTrue();
        root.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()).ShouldContain("private_key_jwt");
        root.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString())
            .ShouldContain("urn:ietf:params:oauth:grant-type:token-exchange");
    }

    // ---------- private_key_jwt ----------

    [Fact]
    public async Task Client_authenticates_with_private_key_jwt_instead_of_a_secret()
    {
        var client = new OidcTestClient(fixture.CreateClient());
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scopes.ApiRead };
        ClientAssertion.AddTo(form, IdentityServerFixture.KeyServiceClientId, fixture.CreateClientAssertion(IdentityServerFixture.KeyServiceClientId));

        var response = await client.TokenAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, response.Body.ToString());
        (await client.ValidateAccessTokenAsync(response.AccessToken)).Subject.ShouldBe(IdentityServerFixture.KeyServiceClientId);
    }

    [Fact]
    public async Task Assertion_signed_with_another_key_is_rejected()
    {
        var client = new OidcTestClient(fixture.CreateClient());
        using var rsa = RSA.Create(2048);
        var foreign = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "attacker" }, SecurityAlgorithms.RsaSha256);
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scopes.ApiRead };
        ClientAssertion.AddTo(form, IdentityServerFixture.KeyServiceClientId, fixture.CreateClientAssertion(IdentityServerFixture.KeyServiceClientId, foreign));

        var response = await client.TokenAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Error.ShouldBe("invalid_client");
    }

    [Fact]
    public async Task Key_based_client_cannot_fall_back_to_a_secret()
    {
        var client = new OidcTestClient(fixture.CreateClient());

        var response = await client.ClientCredentialsAsync(IdentityServerFixture.KeyServiceClientId, "guessed-secret", Scopes.ApiRead);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Error.ShouldBe("invalid_client");
    }

    [Fact]
    public async Task Client_assertion_cannot_be_replayed()
    {
        var client = new OidcTestClient(fixture.CreateClient());
        var assertion = fixture.CreateClientAssertion(IdentityServerFixture.KeyServiceClientId);

        Dictionary<string, string> Form()
        {
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = Scopes.ApiRead };
            ClientAssertion.AddTo(form, IdentityServerFixture.KeyServiceClientId, assertion);
            return form;
        }

        (await client.TokenAsync(Form())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await client.TokenAsync(Form());
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        replay.Error.ShouldBe("invalid_client");
    }

    // ---------- PAR ----------

    [Fact]
    public async Task Pushed_authorization_request_flow_issues_tokens()
    {
        var user = await fixture.CreateUserAsync("par-user", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var (verifier, challenge) = Pkce();

        using var push = await client.Http.PostAsync("/connect/par", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = IdentityServerFixture.ParClientId,
            ["client_secret"] = IdentityServerFixture.WebClientSecret,
            ["response_type"] = "code",
            ["scope"] = $"openid {Scopes.ApiRead}",
            ["redirect_uri"] = IdentityServerFixture.RedirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "s",
        }), Ct);

        push.StatusCode.ShouldBe(HttpStatusCode.Created, await push.Content.ReadAsStringAsync(Ct));
        var pushed = await push.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var requestUri = pushed.GetProperty("request_uri").GetString()!;
        pushed.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);

        var authorizeUrl = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = IdentityServerFixture.ParClientId,
            ["request_uri"] = requestUri,
        });
        var code = await GetCodeAsync(client, authorizeUrl, user.Email!);

        var tokens = await client.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = IdentityServerFixture.RedirectUri,
            ["client_id"] = IdentityServerFixture.ParClientId,
            ["client_secret"] = IdentityServerFixture.WebClientSecret,
            ["code_verifier"] = verifier,
        });

        tokens.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.Body.ToString());
    }

    [Fact]
    public async Task Client_requiring_par_cannot_send_front_channel_parameters()
    {
        var user = await fixture.CreateUserAsync("par-bypass", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());

        var (response, redirect) = await client.FollowAsync(
            AuthorizeUrl(IdentityServerFixture.ParClientId, IdentityServerFixture.RedirectUri, "openid api.read", Pkce().Challenge),
            user.Email!);

        var error = redirect is not null
            ? QueryHelpers.ParseQuery(redirect.Query)["error"].ToString()
            : await response.Content.ReadAsStringAsync(Ct);
        error.ShouldContain("invalid_request");
    }

    // ---------- DPoP ----------

    [Fact]
    public async Task Mobile_client_gets_dpop_bound_tokens_and_refresh_requires_the_same_key()
    {
        var user = await fixture.CreateUserAsync("dpop-user", [(fixture.TenantAlpha, Roles.Student)]);
        var client = new OidcTestClient(fixture.CreateClient());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (verifier, challenge) = Pkce();

        var code = await GetCodeAsync(client,
            AuthorizeUrl(IdentityServerFixture.MobileClientId, IdentityServerFixture.MobileRedirectUri, "openid offline_access api.read", challenge),
            user.Email!);

        var redeem = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = IdentityServerFixture.MobileRedirectUri,
            ["client_id"] = IdentityServerFixture.MobileClientId,
            ["code_verifier"] = verifier,
        };

        // Required for this client: no proof → rejected (the code is not consumed by a rejected DPoP check).
        var withoutProof = await client.TokenAsync(redeem);
        withoutProof.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        withoutProof.Error.ShouldBe(DPoPConstants.InvalidProof);

        var tokens = await client.TokenAsync(redeem, DPoPProofFactory.Create(key, "POST", TokenEndpoint, DateTimeOffset.UtcNow));
        tokens.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.Body.ToString());
        tokens.Get("token_type").ShouldBe("DPoP");

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken);
        DPoPProofValidator.ReadThumbprint(jwt.GetClaim("cnf").Value).ShouldBe(DPoPProofFactory.Thumbprint(key));

        var refresh = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken,
            ["client_id"] = IdentityServerFixture.MobileClientId,
        };

        // Stolen refresh token without the private key is useless.
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stolen = await client.TokenAsync(refresh, DPoPProofFactory.Create(attackerKey, "POST", TokenEndpoint, DateTimeOffset.UtcNow));
        stolen.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        stolen.Error.ShouldBe(DPoPConstants.InvalidProof);

        var proof = DPoPProofFactory.Create(key, "POST", TokenEndpoint, DateTimeOffset.UtcNow);
        var refreshed = await client.TokenAsync(refresh, proof);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, refreshed.Body.ToString());
        refreshed.Get("token_type").ShouldBe("DPoP");

        // Proofs are single use (jti replay protection).
        var replay = await client.TokenAsync(new Dictionary<string, string>(refresh) { ["refresh_token"] = refreshed.RefreshToken }, proof);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        replay.Error.ShouldBe(DPoPConstants.InvalidProof);
    }

    [Theory]
    [InlineData("wrong-method")]
    [InlineData("wrong-uri")]
    [InlineData("stale")]
    public async Task Malformed_dpop_proofs_are_rejected(string scenario)
    {
        var client = new OidcTestClient(fixture.CreateClient());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var proof = scenario switch
        {
            "wrong-method" => DPoPProofFactory.Create(key, "GET", TokenEndpoint, DateTimeOffset.UtcNow),
            "wrong-uri" => DPoPProofFactory.Create(key, "POST", new Uri("https://evil.test/connect/token"), DateTimeOffset.UtcNow),
            _ => DPoPProofFactory.Create(key, "POST", TokenEndpoint, DateTimeOffset.UtcNow.AddMinutes(-10)),
        };

        var response = await client.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = IdentityServerFixture.PlatformServiceClientId,
            ["client_secret"] = IdentityServerFixture.ServiceClientSecret,
            ["scope"] = Scopes.ApiSync,
        }, proof);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, scenario);
        response.Error.ShouldBe(DPoPConstants.InvalidProof);
    }

    // ---------- Token exchange ----------

    [Fact]
    public async Task Resource_server_exchanges_a_user_token_for_a_delegated_downstream_token()
    {
        var user = await fixture.CreateUserAsync("delegator", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var userToken = (await client.SignInAsync(user.Email!, scope: "openid api.read")).AccessToken;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = userToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["scope"] = Scopes.ReportingRead,
        };
        ClientAssertion.AddTo(form, IdentityServerFixture.ResourceClientId, fixture.CreateClientAssertion(IdentityServerFixture.ResourceClientId));

        var exchanged = await client.TokenAsync(form);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, exchanged.Body.ToString());
        exchanged.Get("issued_token_type").ShouldBe("urn:ietf:params:oauth:token-type:access_token");
        var delegated = new JsonWebTokenHandler().ReadJsonWebToken(exchanged.AccessToken);
        var original = new JsonWebTokenHandler().ReadJsonWebToken(userToken);

        delegated.Subject.ShouldBe(user.Id.ToString(CultureInfo.InvariantCulture));
        delegated.Audiences.ShouldBe([Resources.Reporting]);
        delegated.GetClaim("scope").Value.ShouldBe(Scopes.ReportingRead);
        delegated.GetClaim(EduEcoClaimTypes.TenantId).Value.ShouldBe(fixture.TenantAlpha.Id.ToString(CultureInfo.InvariantCulture));
        JsonDocument.Parse(delegated.GetClaim("act").Value).RootElement.GetProperty("sub").GetString().ShouldBe(IdentityServerFixture.ResourceClientId);
        delegated.ValidTo.ShouldBeLessThanOrEqualTo(original.ValidTo.AddSeconds(1), "delegated tokens never outlive the subject token");
    }

    [Fact]
    public async Task Resource_server_can_exchange_a_dpop_bound_user_token()
    {
        // Regression: OpenIddict rejected cnf.jkt (DPoP) confirmations with a server error when validating the subject token.
        var user = await fixture.CreateUserAsync("dpop-delegator", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (verifier, challenge) = Pkce();
        var code = await GetCodeAsync(client,
            AuthorizeUrl(IdentityServerFixture.MobileClientId, IdentityServerFixture.MobileRedirectUri, "openid api.read", challenge),
            user.Email!);
        var tokens = await client.TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = IdentityServerFixture.MobileRedirectUri,
            ["client_id"] = IdentityServerFixture.MobileClientId,
            ["code_verifier"] = verifier,
        }, DPoPProofFactory.Create(key, "POST", TokenEndpoint, DateTimeOffset.UtcNow));
        tokens.Get("token_type").ShouldBe("DPoP");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = tokens.AccessToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["scope"] = Scopes.ReportingRead,
        };
        ClientAssertion.AddTo(form, IdentityServerFixture.ResourceClientId, fixture.CreateClientAssertion(IdentityServerFixture.ResourceClientId));

        var exchanged = await client.TokenAsync(form);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, exchanged.Body.ToString());
        new JsonWebTokenHandler().ReadJsonWebToken(exchanged.AccessToken).TryGetClaim("cnf", out _)
            .ShouldBeFalse("the delegated token is a bearer token for the downstream resource");
    }

    [Fact]
    public async Task Token_exchange_cannot_escalate_to_unpermitted_scopes()
    {
        var user = await fixture.CreateUserAsync("escalator", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var userToken = (await client.SignInAsync(user.Email!, scope: "openid api.read")).AccessToken;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["subject_token"] = userToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["scope"] = Scopes.ApiWrite,
        };
        ClientAssertion.AddTo(form, IdentityServerFixture.ResourceClientId, fixture.CreateClientAssertion(IdentityServerFixture.ResourceClientId));

        var response = await client.TokenAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Error.ShouldBeOneOf("invalid_scope", "invalid_request");
    }

    // ---------- Introspection ----------

    [Fact]
    public async Task Resource_server_introspection_reports_revoked_tokens_as_inactive()
    {
        var user = await fixture.CreateUserAsync("introspected", [(fixture.TenantAlpha, Roles.Teacher)]);
        var client = new OidcTestClient(fixture.CreateClient());
        var accessToken = (await client.SignInAsync(user.Email!, scope: "openid api.read")).AccessToken;

        async Task<bool> ActiveAsync()
        {
            var form = new Dictionary<string, string> { ["token"] = accessToken, ["token_type_hint"] = "access_token" };
            ClientAssertion.AddTo(form, IdentityServerFixture.ResourceClientId, fixture.CreateClientAssertion(IdentityServerFixture.ResourceClientId));
            using var response = await client.Http.PostAsync("/connect/introspect", new FormUrlEncodedContent(form), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("active").GetBoolean();
        }

        (await ActiveAsync()).ShouldBeTrue();

        using (var revoke = await client.Http.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["token"] = accessToken,
                   ["token_type_hint"] = "access_token",
                   ["client_id"] = IdentityServerFixture.WebClientId,
                   ["client_secret"] = IdentityServerFixture.WebClientSecret,
               }), Ct))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await ActiveAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Clients_without_introspection_permission_cannot_introspect()
    {
        var client = new OidcTestClient(fixture.CreateClient());
        var token = (await client.ClientCredentialsAsync(IdentityServerFixture.PlatformServiceClientId, IdentityServerFixture.ServiceClientSecret, Scopes.ApiSync)).AccessToken;

        using var response = await client.Http.PostAsync("/connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = IdentityServerFixture.PlatformServiceClientId,
            ["client_secret"] = IdentityServerFixture.ServiceClientSecret,
        }), Ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
