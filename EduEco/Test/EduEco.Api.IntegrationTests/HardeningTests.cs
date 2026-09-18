using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EduEco.Core.Authorization;
using EduEco.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EduEco.Api.IntegrationTests;

/// <summary>DPoP-bound tokens (RFC 9449), introspection revocation check (RFC 7662), scale-out cache invalidation.</summary>
[Collection(ApiCollection.Name)]
public sealed class HardeningTests(ApiFixture fixture)
{
    private static readonly Uri MeUri = new("https://api.test/api/v1/me");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private string BoundToken(ECDsa key) =>
        TestTokens.Mint(fixture.SigningCertificate, new Dictionary<string, object>(TestTokens.ServiceClaims("mobile", Scopes.ApiRead, fixture.TenantAlpha.Id))
        {
            ["cnf"] = new Dictionary<string, object> { ["jkt"] = DPoPProofFactory.Thumbprint(key) },
            ["jti"] = Guid.NewGuid().ToString("N"),
        });

    private HttpRequestMessage DPoPRequest(string token, string? proof, string scheme = "DPoP")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, MeUri);
        request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {token}");
        if (proof is not null)
        {
            request.Headers.Add("DPoP", proof);
        }

        return request;
    }

    [Fact]
    public async Task Dpop_bound_token_with_valid_proof_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = BoundToken(key);
        using var client = fixture.CreateApiClient();

        using var request = DPoPRequest(token, DPoPProofFactory.Create(key, "GET", MeUri, DateTimeOffset.UtcNow, token));
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Dpop_bound_token_presented_as_bearer_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = fixture.CreateApiClient(BoundToken(key));

        using var response = await client.GetAsync("api/v1/me", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Bearer");
    }

    public static TheoryData<string> BadProofs => ["missing", "other-token", "other-key", "other-url", "other-method"];

    [Theory]
    [MemberData(nameof(BadProofs))]
    public async Task Dpop_requests_with_bad_proofs_are_rejected(string scenario)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = BoundToken(key);
        var now = DateTimeOffset.UtcNow;
        var proof = scenario switch
        {
            "missing" => null,
            "other-token" => DPoPProofFactory.Create(key, "GET", MeUri, now, BoundToken(key)),
            "other-key" => DPoPProofFactory.Create(otherKey, "GET", MeUri, now, token),
            "other-url" => DPoPProofFactory.Create(key, "GET", new Uri("https://api.test/api/v1/tenants/current"), now, token),
            _ => DPoPProofFactory.Create(key, "POST", MeUri, now, token),
        };

        using var client = fixture.CreateApiClient();
        using var request = DPoPRequest(token, proof);
        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, scenario);
        var challenge = response.Headers.WwwAuthenticate.Single();
        challenge.Scheme.ShouldBe("DPoP");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("error=\"invalid_dpop_proof\"");
    }

    [Fact]
    public async Task Dpop_proof_replayed_on_another_instance_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = BoundToken(key);
        var proof = DPoPProofFactory.Create(key, "GET", MeUri, DateTimeOffset.UtcNow, token);

        using (var first = fixture.CreateApiClient())
        using (var request = DPoPRequest(token, proof))
        {
            (await first.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var second = fixture.CreateApiClient(instanceB: true);
        using var replay = DPoPRequest(token, proof);
        (await second.SendAsync(replay, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "Redis-backed replay cache is cluster-wide");
    }

    [Fact]
    public async Task Unbound_token_presented_with_dpop_scheme_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = TestTokens.Mint(fixture.SigningCertificate, TestTokens.ServiceClaims("minted", Scopes.ApiRead, fixture.TenantAlpha.Id));
        using var client = fixture.CreateApiClient();

        using var request = DPoPRequest(token, DPoPProofFactory.Create(key, "GET", MeUri, DateTimeOffset.UtcNow, token));
        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoked_token_is_refused_for_sensitive_operations_but_still_valid_for_reads()
    {
        var admin = await fixture.CreateUserAsync("revoked-admin", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        var newcomer = await fixture.CreateUserAsync("revoked-newcomer");
        var token = await fixture.GetUserTokenAsync(admin);
        using var client = fixture.CreateApiClient(token);

        using var identity = fixture.Identity.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(ApiFixture.Issuer) });
        using (var revoke = await identity.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["token"] = token,
                   ["token_type_hint"] = "access_token",
                   ["client_id"] = ApiFixture.WebClientId,
                   ["client_secret"] = ApiFixture.WebClientSecret,
               }), Ct))
        {
            revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Reads rely on signature + expiry only (short-lived token).
        (await client.GetAsync("api/v1/memberships", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Sensitive writes introspect: the revoked token is refused as invalid_token.
        using var write = await client.PostAsJsonAsync("api/v1/memberships", new { userId = newcomer.Id, role = Roles.Teacher }, Ct);
        write.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        write.Headers.WwwAuthenticate.ToString().ShouldContain("error=\"invalid_token\"");
        (await write.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("token_revoked");
    }

    [Fact]
    public async Task Permission_revocation_on_one_instance_reaches_the_other_instance()
    {
        var adminA = await fixture.CreateUserAsync("scale-revoker", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        var adminB = await fixture.CreateUserAsync("scale-revokee", [(fixture.TenantAlpha, Roles.TenantAdmin)]);
        using var clientA = fixture.CreateApiClient(await fixture.GetUserTokenAsync(adminA));
        using var clientB = fixture.CreateApiClient(await fixture.GetUserTokenAsync(adminB), instanceB: true);

        // Warm B's permission cache on instance B.
        var page = await clientB.GetFromJsonAsync<JsonElement>("api/v1/memberships?pageSize=200", Ct);
        var membershipB = page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("userId").GetInt64() == adminB.Id).GetProperty("id").GetInt64();

        // Revoke through instance A.
        (await clientA.DeleteAsync($"api/v1/memberships/{membershipB}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Instance B learns about it through Redis pub/sub (no waiting for the 5 minute TTL).
        HttpStatusCode status = default;
        for (var attempt = 0; attempt < 30 && status != HttpStatusCode.Forbidden; attempt++)
        {
            using var response = await clientB.GetAsync("api/v1/memberships", Ct);
            status = response.StatusCode;
            if (status != HttpStatusCode.Forbidden)
            {
                await Task.Delay(100, Ct);
            }
        }

        status.ShouldBe(HttpStatusCode.Forbidden);
    }
}
