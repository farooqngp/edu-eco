using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EduEco.Infrastructure.Security;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EduEco.AuthE2E;

/// <summary>
/// Runs every check in order. Later checks reuse state from earlier ones (tokens, users); when a prerequisite failed,
/// the dependent check fails with "prerequisite missing" instead of stopping the run.
/// </summary>
internal sealed class Scenarios(Settings settings) : IDisposable
{
    private const string MobileClient = "eduEco-mobile";
    private const string MobileRedirect = "com.eduEco.mobile:/oauth2redirect";
    private const string ServiceClient = "eduEco-svc-dev";
    private const string ApiClient = "eduEco-api";
    private const string BffClient = "eduEco-bff";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const string TokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string UserScopes = "openid profile email offline_access api.read api.write";

    private readonly List<(string Id, string Name, bool Passed, string Detail, TimeSpan Elapsed)> _results = [];
    private readonly HttpClient _identity = Browser.CreateApiClient(settings.Identity);
    private readonly HttpClient _api = Browser.CreateApiClient(settings.Api);
    private readonly Mailbox _mail = new(settings.Mailpit);

    private JsonElement _discovery;
    private string? _serviceToken;
    private ECDsa? _adminKey;
    private TokenSet? _admin;
    private string? _registeredEmail;
    private long? _registeredUserId;
    private string? _registeredPassword;
    private long? _membershipId;

    private string Issuer => _discovery.GetProperty("issuer").GetString()!;

    private Uri TokenEndpoint => new(_discovery.GetProperty("token_endpoint").GetString()!);

    public async Task<int> RunAsync()
    {
        Console.WriteLine($"EduEco auth E2E  identity={settings.Identity}  api={settings.Api}  bff={settings.Bff}  mailpit={settings.Mailpit}");
        Console.WriteLine();

        await Section("A", "Discovery", [
            ("A1", "Discovery advertises PAR, DPoP, token exchange, private_key_jwt, back-channel logout, S256 only", DiscoveryAsync),
            ("A2", "JWKS publishes signing keys", JwksAsync),
        ]);

        await Section("B", "Service client (client_credentials + private_key_jwt)", [
            ("B1", "Token request without client authentication is rejected", ServiceWithoutAuthenticationAsync),
            ("B2", "Client secret is not accepted (no secret registered)", ServiceWithSecretAsync),
            ("B3", "Signed client assertion issues an access token", ServiceTokenAsync),
            ("B4", "Replayed client assertion is rejected", ServiceAssertionReplayAsync),
            ("B5", "API /me: service caller identified, tenant from client registration", ServiceMeAsync),
            ("B6", "API: service client blocked from user-only permission (users.manage)", ServiceForbiddenAsync),
            ("B7", "API: missing token → 401 + WWW-Authenticate", ApiWithoutTokenAsync),
            ("B8", "API: tampered token signature → 401", ApiTamperedTokenAsync),
        ]);

        await Section("C", "Mobile app (authorization code + PKCE + DPoP)", [
            ("C1", "Authorization request without PKCE is rejected", MobileWithoutPkceAsync),
            ("C2", "Code redemption without DPoP proof is rejected (client requires DPoP)", MobileWithoutDPoPAsync),
            ("C3", "Tenant admin signs in; token is DPoP-bound (token_type=DPoP, cnf.jkt)", MobileAdminSignInAsync),
            ("C4", "API accepts DPoP token with a valid proof", MobileApiWithProofAsync),
            ("C5", "API rejects the DPoP token sent as Bearer (no downgrade)", MobileApiAsBearerAsync),
            ("C6", "API rejects a replayed DPoP proof", MobileProofReplayAsync),
        ]);

        await Section("D", "Role/permission enforcement", [
            ("D1", "Tenant admin can list memberships (users.manage)", AdminListsMembershipsAsync),
            ("D2", "Teacher cannot list memberships → 403 permission_denied", TeacherForbiddenAsync),
        ]);

        await Section("E", "API as client: token exchange and introspection", [
            ("E1", "Token exchange (RFC 8693) → reporting token with act claim", TokenExchangeAsync),
            ("E2", "Introspection reports the admin token as active", IntrospectionActiveAsync),
        ]);

        await Section("F", "Registration, email confirmation and tenant onboarding", [
            ("F1", "Register a new account → confirmation email in Mailpit", RegisterAsync),
            ("F2", "Confirm email address from the emailed link", ConfirmEmailAsync),
            ("F3", "Confirmed user without tenant access gets access_denied", NoTenantDeniedAsync),
            ("F4", "Tenant admin grants Student membership (API, introspected)", GrantMembershipAsync),
            ("F5", "New user signs in and has the Student role for the tenant", NewUserSignInAsync),
        ]);

        await Section("G", "Forgot password", [
            ("G1", "Forgot password → reset email → new password set", ResetPasswordAsync),
            ("G2", "Old password no longer works", OldPasswordRejectedAsync),
            ("G3", "New password works", NewPasswordAcceptedAsync),
        ]);

        await Section("H", "Revocation", [
            ("H1", "Revoked access token is refused by [RequireActiveToken] endpoints (token_revoked)", RevokedTokenAsync),
            ("H2", "Cleanup: tenant admin removes the test membership", RemoveMembershipAsync),
            ("H3", "Refresh token rotation keeps the DPoP binding; refresh without proof is rejected", RefreshAsync),
            ("H4", "Reusing a rotated refresh token is detected and rejected", RefreshReuseAsync),
        ]);

        await Section("I", "SPA through the BFF (PAR + private_key_jwt + back-channel logout)", [
            ("I1", "BFF login pushes the request (PAR): browser URL has only client_id + request_uri", BffParAsync),
            ("I2", "BFF session: /bff/user needs X-CSRF; API calls proxied with the user's token", BffSessionAsync),
            ("I3", "Logout in one browser ends the user's BFF session in another browser (back-channel logout)", BffBackchannelLogoutAsync),
        ]);

        return Summary();
    }

    // ───────────────────────────── A. Discovery ─────────────────────────────

    private async Task<string> DiscoveryAsync()
    {
        using var response = await _identity.GetAsync(new Uri(".well-known/openid-configuration", UriKind.Relative));
        _discovery = await Expect.StatusAsync(response, HttpStatusCode.OK);

        Expect.That(_discovery.TryGetProperty("pushed_authorization_request_endpoint", out _), "no pushed_authorization_request_endpoint");
        Expect.That(Contains("grant_types_supported", TokenExchangeGrant), "token exchange grant not advertised");
        Expect.That(Contains("token_endpoint_auth_methods_supported", "private_key_jwt"), "private_key_jwt not advertised");
        Expect.That(_discovery.TryGetProperty("dpop_signing_alg_values_supported", out var algs) && algs.GetArrayLength() > 0, "DPoP algorithms not advertised");
        Expect.That(_discovery.TryGetProperty("backchannel_logout_supported", out var bcl) && bcl.GetBoolean(), "back-channel logout not advertised");
        Expect.That(_discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()).SequenceEqual(["S256"]), "PKCE methods other than S256 advertised");
        Expect.That(!Contains("grant_types_supported", "password") && !Contains("grant_types_supported", "implicit"), "legacy grant advertised");
        return $"issuer {Issuer}";
    }

    private async Task<string> JwksAsync()
    {
        using var response = await _identity.GetAsync(new Uri(_discovery.GetProperty("jwks_uri").GetString()!));
        var jwks = await Expect.StatusAsync(response, HttpStatusCode.OK);
        var keys = jwks.GetProperty("keys");
        Expect.That(keys.GetArrayLength() > 0, "no keys");
        Expect.That(keys.EnumerateArray().All(k => !k.TryGetProperty("d", out _)), "private key material published");
        return $"{keys.GetArrayLength()} key(s)";
    }

    // ───────────────────────────── B. Service client ─────────────────────────────

    private async Task<string> ServiceWithoutAuthenticationAsync()
    {
        var error = await TokenErrorAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = ServiceClient, ["scope"] = "api.read" });
        Expect.That(error is "invalid_client" or "invalid_request", $"expected invalid_client/invalid_request, got {error}");
        return error!;
    }

    private async Task<string> ServiceWithSecretAsync()
    {
        var error = await TokenErrorAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ServiceClient,
            ["client_secret"] = "guessed-secret",
            ["scope"] = "api.read",
        });
        Expect.That(error == "invalid_client", $"expected invalid_client, got {error}");
        return error!;
    }

    private string? _lastServiceAssertion;

    private async Task<string> ServiceTokenAsync()
    {
        _lastServiceAssertion = ClientAssertion.Create(ServiceClient, Issuer, settings.ClientCredentials("svc-dev-client.pfx"), DateTimeOffset.UtcNow);
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "api.read" };
        ClientAssertion.AddTo(form, ServiceClient, _lastServiceAssertion);

        var body = await TokenAsync(form, HttpStatusCode.OK);
        _serviceToken = body.GetProperty("access_token").GetString();
        var token = new JsonWebToken(_serviceToken);
        Expect.That(token.Typ == "at+jwt", $"typ {token.Typ}");
        Expect.That(token.Audiences.Contains("eduEco-api"), "aud is not eduEco-api");
        return $"token_type={body.GetProperty("token_type").GetString()}, expires_in={body.GetProperty("expires_in").GetInt32()} s";
    }

    private async Task<string> ServiceAssertionReplayAsync()
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "api.read" };
        ClientAssertion.AddTo(form, ServiceClient, Expect.NotNull(_lastServiceAssertion, "prerequisite missing (B3)"));
        var error = await TokenErrorAsync(form);
        Expect.That(error == "invalid_client", $"expected invalid_client, got {error}");
        return "assertion jti already used";
    }

    private async Task<string> ServiceMeAsync()
    {
        var me = await ApiAsync(HttpMethod.Get, "api/v1/me", Expect.NotNull(_serviceToken, "prerequisite missing (B3)"), HttpStatusCode.OK);
        Expect.That(me.GetProperty("isServiceClient").GetBoolean(), "not identified as service client");
        Expect.That(me.GetProperty("tenantId").ValueKind == JsonValueKind.Number, "no tenant on service token");
        return $"clientId={me.GetProperty("clientId").GetString()}, tenantId={me.GetProperty("tenantId")}";
    }

    private async Task<string> ServiceForbiddenAsync()
    {
        var problem = await ApiAsync(HttpMethod.Get, "api/v1/memberships", Expect.NotNull(_serviceToken, "prerequisite missing (B3)"), HttpStatusCode.Forbidden);
        return $"403 {problem.GetProperty("code").GetString()}";
    }

    private async Task<string> ApiWithoutTokenAsync()
    {
        using var response = await _api.GetAsync(new Uri("api/v1/me", UriKind.Relative));
        await Expect.StatusAsync(response, HttpStatusCode.Unauthorized);
        Expect.That(response.Headers.WwwAuthenticate.Count > 0, "no WWW-Authenticate header");
        return response.Headers.WwwAuthenticate.ToString();
    }

    private async Task<string> ApiTamperedTokenAsync()
    {
        var token = Expect.NotNull(_serviceToken, "prerequisite missing (B3)");
        var tampered = token[..^4] + (token[^4] == 'A' ? "BBBB" : "AAAA");
        await ApiAsync(HttpMethod.Get, "api/v1/me", tampered, HttpStatusCode.Unauthorized);
        return "signature check failed as expected";
    }

    // ───────────────────────────── C. Mobile app ─────────────────────────────

    private async Task<string> MobileWithoutPkceAsync()
    {
        using var browser = new Browser(settings.Identity);
        var url = Query.Build(settings.Identity, "connect/authorize", new Dictionary<string, string>
        {
            ["client_id"] = MobileClient,
            ["response_type"] = "code",
            ["scope"] = UserScopes,
            ["redirect_uri"] = MobileRedirect,
            ["state"] = "no-pkce",
        });

        using var response = await browser.Http.GetAsync(new Uri(url));
        var location = response.Headers.Location is { } l ? new Uri(new Uri(url), l) : null;
        var error = location is null ? null : Query.Get(location, "error");
        Expect.That(error == "invalid_request" || response.StatusCode == HttpStatusCode.BadRequest,
            $"expected invalid_request, got {(int)response.StatusCode} {location}");
        return $"error={error ?? "400"}";
    }

    private async Task<string> MobileWithoutDPoPAsync()
    {
        using var browser = new Browser(settings.Identity);
        var (code, verifier) = await AuthorizeMobileAsync(browser, Settings.TenantAdmin, settings.DevUserPassword);
        var error = await TokenErrorAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = MobileRedirect,
            ["client_id"] = MobileClient,
            ["code_verifier"] = verifier,
        });
        Expect.That(error == DPoPConstants.InvalidProof, $"expected {DPoPConstants.InvalidProof}, got {error}");
        return error!;
    }

    private async Task<string> MobileAdminSignInAsync()
    {
        _adminKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _admin = await SignInMobileAsync(Settings.TenantAdmin, settings.DevUserPassword, _adminKey);
        var cnf = new JsonWebToken(_admin.AccessToken).GetClaim("cnf").Value;
        Expect.That(cnf.Contains(DPoPProofFactory.Thumbprint(_adminKey), StringComparison.Ordinal), "cnf.jkt does not match the DPoP key");
        return $"token_type={_admin.TokenType}, cnf={cnf}";
    }

    private async Task<string> MobileApiWithProofAsync()
    {
        var admin = Expect.NotNull(_admin, "prerequisite missing (C3)");
        var me = await ApiAsync(HttpMethod.Get, "api/v1/me", admin.AccessToken, HttpStatusCode.OK, _adminKey);
        var permissions = me.GetProperty("effectivePermissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Expect.That(permissions.Contains("users.manage"), "tenant admin lacks users.manage");
        return $"roles={string.Join(',', me.GetProperty("roles").EnumerateArray())}, {permissions.Count} permission(s)";
    }

    private async Task<string> MobileApiAsBearerAsync()
    {
        await ApiAsync(HttpMethod.Get, "api/v1/me", Expect.NotNull(_admin, "prerequisite missing (C3)").AccessToken, HttpStatusCode.Unauthorized);
        return "401";
    }

    private async Task<string> MobileProofReplayAsync()
    {
        var admin = Expect.NotNull(_admin, "prerequisite missing (C3)");
        var uri = new Uri(settings.Api, "api/v1/me");
        var proof = DPoPProofFactory.Create(_adminKey!, "GET", uri, DateTimeOffset.UtcNow, admin.AccessToken);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = Http.Scheme("DPoP", admin.AccessToken);
            request.Headers.Add("DPoP", proof);
            using var response = await _api.SendAsync(request);
            var expected = attempt == 0 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
            await Expect.StatusAsync(response, expected);
        }

        return "first use 200, replay 401";
    }

    // ───────────────────────────── D. Permissions ─────────────────────────────

    private async Task<string> AdminListsMembershipsAsync()
    {
        var page = await ApiAsync(HttpMethod.Get, "api/v1/memberships", Expect.NotNull(_admin, "prerequisite missing (C3)").AccessToken, HttpStatusCode.OK, _adminKey);
        return $"{page.GetProperty("items").GetArrayLength()} membership(s) in tenant";
    }

    private async Task<string> TeacherForbiddenAsync()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var teacher = await SignInMobileAsync(Settings.Teacher, settings.DevUserPassword, key);
        var problem = await ApiAsync(HttpMethod.Get, "api/v1/memberships", teacher.AccessToken, HttpStatusCode.Forbidden, key);
        var code = problem.GetProperty("code").GetString();
        Expect.That(code == "permission_denied", $"code {code}");
        return $"403 {code}";
    }

    // ───────────────────────────── E. Token exchange / introspection ─────────────────────────────

    private async Task<string> TokenExchangeAsync()
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = TokenExchangeGrant,
            ["subject_token"] = Expect.NotNull(_admin, "prerequisite missing (C3)").AccessToken,
            ["subject_token_type"] = AccessTokenType,
            ["scope"] = "reporting.read",
        };
        ClientAssertion.AddTo(form, ApiClient, ClientAssertion.Create(ApiClient, Issuer, settings.ClientCredentials("api-client.pfx"), DateTimeOffset.UtcNow));

        var body = await TokenAsync(form, HttpStatusCode.OK);
        var token = new JsonWebToken(body.GetProperty("access_token").GetString());
        Expect.That(token.Audiences.Contains("eduEco-reporting"), "audience is not eduEco-reporting");
        var act = token.GetClaim("act").Value;
        Expect.That(act.Contains(ApiClient, StringComparison.Ordinal), $"act claim {act}");
        return $"aud=eduEco-reporting, sub={token.Subject}, act={act}";
    }

    private async Task<string> IntrospectionActiveAsync()
    {
        var active = await IntrospectAsync(Expect.NotNull(_admin, "prerequisite missing (C3)").AccessToken);
        Expect.That(active, "token reported inactive");
        return "active=true";
    }

    // ───────────────────────────── F. Registration ─────────────────────────────

    private async Task<string> RegisterAsync()
    {
        _registeredEmail = $"e2e-{Guid.NewGuid():N}"[..16] + "@demo.eduEco.local";
        var password = "E2e-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "!a";

        using var browser = new Browser(settings.Identity);
        using var response = await browser.PostFormAsync(new Uri(settings.Identity, "Account/Register"), new Dictionary<string, string>
        {
            ["Input.Email"] = _registeredEmail,
            ["Input.DisplayName"] = "E2E Student",
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = password,
        });
        var html = await response.Content.ReadAsStringAsync();
        Expect.That(response.StatusCode == HttpStatusCode.OK && html.Contains("Check your email", StringComparison.Ordinal),
            response.StatusCode == HttpStatusCode.NotFound ? "registration disabled (IdentityServer:AllowSelfRegistration)" : $"unexpected {(int)response.StatusCode}");

        _registeredPassword = password;
        var link = await _mail.WaitForLinkAsync(_registeredEmail, "Confirm", TimeSpan.FromSeconds(15));
        _confirmationLink = link;
        _registeredUserId = long.Parse(Query.Get(link, "userId")!, System.Globalization.CultureInfo.InvariantCulture);
        return $"{_registeredEmail} (user {_registeredUserId})";
    }

    private Uri? _confirmationLink;

    private async Task<string> ConfirmEmailAsync()
    {
        using var browser = new Browser(settings.Identity);
        var html = await browser.Http.GetStringAsync(Expect.NotNull(_confirmationLink, "prerequisite missing (F1)"));
        Expect.That(html.Contains("Your email address is confirmed", StringComparison.Ordinal), "confirmation page did not confirm");
        return "confirmed";
    }

    private async Task<string> NoTenantDeniedAsync()
    {
        using var browser = new Browser(settings.Identity);
        var (verifier, challenge) = Pkce.Create();
        _ = verifier;
        var callback = await browser.AuthorizeAsync(MobileAuthorizeUrl(challenge), Expect.NotNull(_registeredEmail, "prerequisite missing (F1)"), _registeredPassword!);
        var error = Query.Get(callback, "error");
        Expect.That(error == "access_denied", $"expected access_denied, got {callback}");
        return $"error={error}: {Query.Get(callback, "error_description")}";
    }

    private async Task<string> GrantMembershipAsync()
    {
        var body = await ApiAsync(
            HttpMethod.Post,
            "api/v1/memberships",
            Expect.NotNull(_admin, "prerequisite missing (C3)").AccessToken,
            HttpStatusCode.Created,
            _adminKey,
            new { userId = _registeredUserId ?? throw new CheckFailedException("prerequisite missing (F1)"), role = "Student" });
        _membershipId = body.GetProperty("id").GetInt64();
        return $"membership {_membershipId} ({body.GetProperty("role").GetString()})";
    }

    private async Task<string> NewUserSignInAsync()
    {
        Expect.NotNull(_membershipId is null ? null : "ok", "prerequisite missing (F4)");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tokens = await SignInMobileAsync(_registeredEmail!, _registeredPassword!, key);
        var me = await ApiAsync(HttpMethod.Get, "api/v1/me", tokens.AccessToken, HttpStatusCode.OK, key);
        var roles = me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Expect.That(roles.Contains("Student"), $"roles {string.Join(',', roles)}");
        return $"roles={string.Join(',', roles)}, tenantId={me.GetProperty("tenantId")}";
    }

    // ───────────────────────────── G. Forgot password ─────────────────────────────

    private string? _oldPassword;

    private async Task<string> ResetPasswordAsync()
    {
        var email = Expect.NotNull(_registeredEmail, "prerequisite missing (F1)");
        using var browser = new Browser(settings.Identity);
        using (var forgot = await browser.PostFormAsync(new Uri(settings.Identity, "Account/ForgotPassword"), new Dictionary<string, string> { ["email"] = email }))
        {
            await Expect.StatusAsync(forgot, HttpStatusCode.OK);
        }

        var link = await _mail.WaitForLinkAsync(email, "Reset", TimeSpan.FromSeconds(15));
        var newPassword = "E2e-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "!b";
        using var reset = await browser.PostFormAsync(link, new Dictionary<string, string>
        {
            ["userId"] = Query.Get(link, "userId")!,
            ["code"] = Query.Get(link, "code")!,
            ["password"] = newPassword,
            ["confirmPassword"] = newPassword,
        });
        var html = await reset.Content.ReadAsStringAsync();
        Expect.That(html.Contains("Your password has been reset", StringComparison.Ordinal), "reset page did not confirm");

        _oldPassword = _registeredPassword;
        _registeredPassword = newPassword;
        return "password changed; existing sessions signed out";
    }

    private async Task<string> OldPasswordRejectedAsync()
    {
        using var browser = new Browser(settings.Identity);
        try
        {
            await browser.AuthorizeAsync(MobileAuthorizeUrl(Pkce.Create().Challenge), Expect.NotNull(_registeredEmail, "prerequisite missing"), Expect.NotNull(_oldPassword, "prerequisite missing (G1)"));
        }
        catch (CheckFailedException ex) when (ex.Message.Contains("/Account/Login", StringComparison.Ordinal))
        {
            return "login page refused the old password";
        }

        throw new CheckFailedException("old password still signs in");
    }

    private async Task<string> NewPasswordAcceptedAsync()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tokens = await SignInMobileAsync(Expect.NotNull(_registeredEmail, "prerequisite missing"), Expect.NotNull(_oldPassword is null ? null : _registeredPassword, "prerequisite missing (G1)"), key);
        return $"signed in (token_type={tokens.TokenType})";
    }

    // ───────────────────────────── H. Revocation / refresh ─────────────────────────────

    private async Task<string> RevokedTokenAsync()
    {
        Expect.NotNull(_membershipId is null ? null : "ok", "prerequisite missing (F4)");

        // A fresh token (never introspected, so no cached "active" result), revoked before use.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tokens = await SignInMobileAsync(Settings.TenantAdmin, settings.DevUserPassword, key);
        using (var revoke = await _identity.PostAsync(new Uri(_discovery.GetProperty("revocation_endpoint").GetString()!), new FormUrlEncodedContent(new Dictionary<string, string>
               {
                   ["client_id"] = MobileClient,
                   ["token"] = tokens.AccessToken,
                   ["token_type_hint"] = "access_token",
               })))
        {
            await Expect.StatusAsync(revoke, HttpStatusCode.OK);
        }

        // Reads rely on signature + expiry; the sensitive write introspects and refuses.
        await ApiAsync(HttpMethod.Get, "api/v1/me", tokens.AccessToken, HttpStatusCode.OK, key);
        var problem = await ApiAsync(HttpMethod.Delete, $"api/v1/memberships/{_membershipId}", tokens.AccessToken, HttpStatusCode.Unauthorized, key);
        var code = problem.GetProperty("code").GetString();
        Expect.That(code == "token_revoked", $"code {code}");
        return "GET /me 200 (signature valid), DELETE 401 token_revoked";
    }

    private async Task<string> RemoveMembershipAsync()
    {
        var id = _membershipId ?? throw new CheckFailedException("prerequisite missing (F4)");
        await ApiAsync(HttpMethod.Delete, $"api/v1/memberships/{id}", Expect.NotNull(_admin, "prerequisite missing (C3)").AccessToken, HttpStatusCode.NoContent, _adminKey);
        return $"membership {id} deleted";
    }

    private string? _rotatedRefreshToken;

    private async Task<string> RefreshAsync()
    {
        var admin = Expect.NotNull(_admin, "prerequisite missing (C3)");
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = admin.RefreshToken, ["client_id"] = MobileClient };

        var withoutProof = await TokenErrorAsync(form);
        Expect.That(withoutProof == DPoPConstants.InvalidProof, $"refresh without proof: {withoutProof}");

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongKey = await TokenErrorAsync(form, DPoPProofFactory.Create(otherKey, "POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Expect.That(wrongKey is not null, "refresh with a different DPoP key succeeded");

        var body = await TokenAsync(form, HttpStatusCode.OK, DPoPProofFactory.Create(_adminKey!, "POST", TokenEndpoint, DateTimeOffset.UtcNow));
        var newRefresh = body.GetProperty("refresh_token").GetString()!;
        Expect.That(newRefresh != admin.RefreshToken, "refresh token not rotated");
        _rotatedRefreshToken = admin.RefreshToken;
        _admin = admin with { AccessToken = body.GetProperty("access_token").GetString()!, RefreshToken = newRefresh };
        return $"rotated; without proof → {withoutProof}, other key → {wrongKey}";
    }

    private async Task<string> RefreshReuseAsync()
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = Expect.NotNull(_rotatedRefreshToken, "prerequisite missing (H3)"),
            ["client_id"] = MobileClient,
        };
        var error = await TokenErrorAsync(form, DPoPProofFactory.Create(_adminKey!, "POST", TokenEndpoint, DateTimeOffset.UtcNow));
        Expect.That(error == "invalid_grant", $"expected invalid_grant, got {error}");
        return "invalid_grant (whole chain revoked)";
    }

    // ───────────────────────────── I. BFF ─────────────────────────────

    private async Task<string> BffParAsync()
    {
        using var browser = new Browser(settings.Identity);
        using var response = await browser.Http.GetAsync(new Uri(settings.Bff, "bff/login?returnUrl=%2F"));
        Expect.That(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"login returned {(int)response.StatusCode}");
        var location = response.Headers.Location!;
        Expect.That(Query.Get(location, "request_uri")?.StartsWith("urn:ietf:params:oauth:request_uri:", StringComparison.Ordinal) == true, "no request_uri");
        Expect.That(Query.Get(location, "code_challenge") is null && Query.Get(location, "redirect_uri") is null, "authorization parameters leaked into the URL");
        Expect.That(Query.Get(location, "client_id") == BffClient, "client_id missing");
        return location.GetLeftPart(UriPartial.Path) + "?client_id=…&request_uri=urn:…";
    }

    private Browser? _laptop;

    private async Task<string> BffSessionAsync()
    {
        _laptop = await BffSignInAsync(Settings.Teacher);

        using (var noCsrf = await _laptop.GetAsync(new Uri(settings.Bff, "bff/user")))
        {
            await Expect.StatusAsync(noCsrf, HttpStatusCode.Forbidden);
        }

        using var user = await _laptop.GetAsync(new Uri(settings.Bff, "bff/user"), csrf: true);
        var body = await Expect.StatusAsync(user, HttpStatusCode.OK);

        using var me = await _laptop.GetAsync(new Uri(settings.Bff, "api/v1/me"), csrf: true);
        var caller = await Expect.StatusAsync(me, HttpStatusCode.OK);
        Expect.That(caller.GetProperty("roles").EnumerateArray().Any(r => r.GetString() == "Teacher"), "proxied call lacks Teacher role");

        var cookies = _laptop.Cookies.GetCookies(settings.Bff).Select(c => c.Name).ToList();
        Expect.That(cookies.Contains("__Host-EduEco.Bff"), "session cookie missing");
        return $"sub={body.GetProperty("subject").GetString()}, /api/v1/me via BFF 200, cookie __Host-EduEco.Bff only (no tokens)";
    }

    private async Task<string> BffBackchannelLogoutAsync()
    {
        var laptop = Expect.NotNull(_laptop, "prerequisite missing (I2)");
        using var tablet = await BffSignInAsync(Settings.Teacher);

        using var user = await laptop.GetAsync(new Uri(settings.Bff, "bff/user"), csrf: true);
        var logoutUrl = (await Expect.StatusAsync(user, HttpStatusCode.OK)).GetProperty("logoutUrl").GetString()!;

        using var logout = await laptop.GetAsync(new Uri(settings.Bff, logoutUrl));
        var endSession = new Uri(settings.Bff, logout.Headers.Location!);
        Expect.That(endSession.AbsolutePath.EndsWith("/connect/endsession", StringComparison.Ordinal), $"logout redirected to {endSession}");
        using (await laptop.Http.GetAsync(endSession))
        {
        }

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            using var check = await tablet.GetAsync(new Uri(settings.Bff, "bff/user"), csrf: true);
            if (check.StatusCode == HttpStatusCode.Unauthorized)
            {
                laptop.Dispose();
                _laptop = null;
                return $"tablet session ended after {watch.Elapsed.TotalSeconds:0.0} s";
            }

            await Task.Delay(500);
        }

        throw new CheckFailedException("the other browser session is still active after 20 s (check identity logs for back-channel delivery)");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private sealed record TokenSet(string AccessToken, string RefreshToken, string TokenType);

    private Uri MobileAuthorizeUrl(string challenge) => new(Query.Build(settings.Identity, "connect/authorize", new Dictionary<string, string>
    {
        ["client_id"] = MobileClient,
        ["response_type"] = "code",
        ["scope"] = UserScopes,
        ["redirect_uri"] = MobileRedirect,
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
        ["state"] = Guid.NewGuid().ToString("N"),
        ["nonce"] = Guid.NewGuid().ToString("N"),
    }));

    private async Task<(string Code, string Verifier)> AuthorizeMobileAsync(Browser browser, string email, string password)
    {
        var (verifier, challenge) = Pkce.Create();
        var callback = await browser.AuthorizeAsync(MobileAuthorizeUrl(challenge), email, password);
        var code = Query.Get(callback, "code");
        Expect.That(code is not null, $"no code: {callback}");
        return (code!, verifier);
    }

    private async Task<TokenSet> SignInMobileAsync(string email, string password, ECDsa key)
    {
        using var browser = new Browser(settings.Identity);
        var (code, verifier) = await AuthorizeMobileAsync(browser, email, password);
        var body = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = MobileRedirect,
            ["client_id"] = MobileClient,
            ["code_verifier"] = verifier,
        }, HttpStatusCode.OK, DPoPProofFactory.Create(key, "POST", TokenEndpoint, DateTimeOffset.UtcNow));

        var tokenType = body.GetProperty("token_type").GetString()!;
        Expect.That(tokenType == "DPoP", $"token_type {tokenType}");
        return new TokenSet(body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!, tokenType);
    }

    private async Task<Browser> BffSignInAsync(string email)
    {
        var browser = new Browser(settings.Identity);
        try
        {
            using var login = await browser.Http.GetAsync(new Uri(settings.Bff, "bff/login?returnUrl=%2F"));
            var callback = await browser.AuthorizeAsync(new Uri(settings.Bff, login.Headers.Location!), email, settings.DevUserPassword);
            Expect.That(Browser.SameOrigin(callback, settings.Bff), $"unexpected callback {callback}");
            using var complete = await browser.Http.GetAsync(callback);
            Expect.That(complete.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"callback returned {(int)complete.StatusCode}");
            Expect.That(complete.Headers.Location?.ToString().Contains("login_error", StringComparison.Ordinal) != true, "BFF reported a login error");
            return browser;
        }
        catch
        {
            browser.Dispose();
            throw;
        }
    }

    private async Task<JsonElement> TokenAsync(Dictionary<string, string> form, HttpStatusCode expected, string? dpopProof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        if (dpopProof is not null)
        {
            request.Headers.Add("DPoP", dpopProof);
        }

        using var response = await _identity.SendAsync(request);
        return await Expect.StatusAsync(response, expected);
    }

    private async Task<string?> TokenErrorAsync(Dictionary<string, string> form, string? dpopProof = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        if (dpopProof is not null)
        {
            request.Headers.Add("DPoP", dpopProof);
        }

        using var response = await _identity.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : $"{(int)response.StatusCode}";
    }

    private async Task<bool> IntrospectAsync(string token)
    {
        var form = new Dictionary<string, string> { ["token"] = token, ["token_type_hint"] = "access_token" };
        ClientAssertion.AddTo(form, ApiClient, ClientAssertion.Create(ApiClient, Issuer, settings.ClientCredentials("api-client.pfx"), DateTimeOffset.UtcNow));
        using var response = await _identity.PostAsync(new Uri(_discovery.GetProperty("introspection_endpoint").GetString()!), new FormUrlEncodedContent(form));
        var body = await Expect.StatusAsync(response, HttpStatusCode.OK);
        return body.GetProperty("active").GetBoolean();
    }

    private async Task<JsonElement> ApiAsync(HttpMethod method, string path, string token, HttpStatusCode expected, ECDsa? dpopKey = null, object? json = null)
    {
        var uri = new Uri(settings.Api, path);
        using var request = new HttpRequestMessage(method, uri);
        if (dpopKey is null)
        {
            request.Headers.Authorization = Http.Scheme("Bearer", token);
        }
        else
        {
            request.Headers.Authorization = Http.Scheme("DPoP", token);
            request.Headers.Add("DPoP", DPoPProofFactory.Create(dpopKey, method.Method, uri, DateTimeOffset.UtcNow, token));
        }

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        using var response = await _api.SendAsync(request);
        return await Expect.StatusAsync(response, expected);
    }

    private bool Contains(string property, string value) =>
        _discovery.TryGetProperty(property, out var values) && values.EnumerateArray().Any(v => v.GetString() == value);

    private async Task Section(string id, string title, (string Id, string Name, Func<Task<string>> Run)[] checks)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"{id}. {title}");
        Console.ResetColor();

        foreach (var (checkId, name, run) in checks)
        {
            var watch = Stopwatch.StartNew();
            bool passed;
            string detail;
            try
            {
                detail = await run();
                passed = true;
            }
            catch (CheckFailedException ex)
            {
                detail = ex.Message;
                passed = false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                detail = $"{ex.GetType().Name}: {ex.Message}";
                passed = false;
            }

            watch.Stop();
            _results.Add((checkId, name, passed, detail, watch.Elapsed));
            Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(passed ? "  PASS " : "  FAIL ");
            Console.ResetColor();
            Console.WriteLine($"{checkId,-3} {name}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"         {Expect.Trim(detail)}");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    private int Summary()
    {
        var failed = _results.Count(r => !r.Passed);
        Console.ForegroundColor = failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"{_results.Count - failed}/{_results.Count} checks passed");
        Console.ResetColor();
        foreach (var result in _results.Where(r => !r.Passed))
        {
            Console.WriteLine($"  FAIL {result.Id} {result.Name}: {result.Detail}");
        }

        return failed == 0 ? 0 : 1;
    }

    public void Dispose()
    {
        _identity.Dispose();
        _api.Dispose();
        _mail.Dispose();
        _adminKey?.Dispose();
        _laptop?.Dispose();
    }
}
