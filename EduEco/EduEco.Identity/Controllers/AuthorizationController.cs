using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using EduEco.Application.Tenants;
using ClientProperties = EduEco.Core.Authorization.ClientProperties;
using EduEcoClaimTypes = EduEco.Core.Authorization.EduEcoClaimTypes;
using EduEco.Core.Identity;
using EduEco.Identity.Auditing;
using EduEco.Identity.Configuration;
using EduEco.Identity.Logout;
using EduEco.Identity.Services;
using EduEco.Infrastructure.Security;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EduEco.Identity.Controllers;

/// <summary>OpenID Connect passthrough endpoints. Protocol validation is performed by OpenIddict before these actions run.</summary>
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class AuthorizationController(
    IOpenIddictApplicationManager applicationManager,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    TokenPrincipalFactory principalFactory,
    TenantAccessResolver tenantAccessResolver,
    ITenantQueries tenantQueries,
    IOptions<IdentityServerOptions> options,
    DPoPProofValidator dpopValidator,
    BackchannelLogoutNotifier logoutNotifier,
    TimeProvider timeProvider,
    ILogger<AuthorizationController> logger) : Controller
{
    /// <summary>Custom authorization request parameter selecting the tenant (tenant code).</summary>
    public const string TenantParameter = "tenant";

    /// <summary>HttpContext item telling the token response handler to answer <c>token_type=DPoP</c>.</summary>
    public const string DPoPBoundItem = "EduEco.DPoPBound";

    /// <summary>HttpContext item carrying the DPoP key thumbprint to the access token (<c>cnf.jkt</c>).</summary>
    public const string DPoPThumbprintItem = "EduEco.DPoPThumbprint";

    /// <summary>Private claim (no destination) binding refresh tokens to a DPoP key.</summary>
    public const string DPoPThumbprintClaim = "eduEco_dpop_jkt";

    /// <summary>RFC 8693 §4.1 actor claim (OpenIddict's Claims.Actor constant is "actor", not the standard "act").</summary>
    public const string ActorClaim = "act";

    /// <summary>JSON claim value type (serialised object claims such as <c>cnf</c> and <c>act</c>).</summary>
    private const string JsonClaimValueType = "JSON";

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var session = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var maxAgeExceeded = request.MaxAge is not null
            && session.Properties?.IssuedUtc is { } issued
            && timeProvider.GetUtcNow() - issued > TimeSpan.FromSeconds(request.MaxAge.Value);

        if (!session.Succeeded || request.HasPromptValue(PromptValues.Login) || maxAgeExceeded)
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Deny(request, Errors.LoginRequired, "The user is not signed in.");
            }

            // Drop prompt=login from the return URL to avoid a login loop once the user has re-authenticated.
            var promptValues = request.GetPromptValues().Remove(PromptValues.Login);
            var returnUrl = await BuildAuthorizeUrlAsync(
                replace: (Parameters.Prompt, promptValues.IsEmpty ? null : string.Join(' ', promptValues)),
                cancellationToken: cancellationToken);

            return Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, IdentityConstants.ApplicationScheme);
        }

        var user = await userManager.GetUserAsync(session.Principal!);
        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            await signInManager.SignOutAsync();
            return Deny(request, Errors.AccessDenied, "The account is disabled.");
        }

        var application = await applicationManager.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("The client application cannot be found.");

        // Only first-party clients (implicit consent) are supported; third-party consent UI is out of scope.
        if (await applicationManager.GetConsentTypeAsync(application, cancellationToken) != ConsentTypes.Implicit)
        {
            return Deny(request, Errors.ConsentRequired, "Interactive consent is not supported for this client.");
        }

        // With PAR the original parameters come from the pushed request; the tenant picker appends its choice to the
        // URL (the tenant is not a security parameter: access is re-validated below and at every redemption).
        var requestedTenant = (string?)request[TenantParameter];
        if (string.IsNullOrWhiteSpace(requestedTenant))
        {
            requestedTenant = Request.Query[TenantParameter].ToString();
        }

        var selection = await tenantAccessResolver.SelectAsync(user, requestedTenant, cancellationToken);
        switch (selection.Kind)
        {
            case TenantSelectionKind.Denied:
                return Deny(request, Errors.AccessDenied, "The user has no access to the requested tenant.");

            case TenantSelectionKind.SelectionRequired when request.HasPromptValue(PromptValues.None):
                return Deny(request, Errors.InteractionRequired, "Tenant selection is required.");

            case TenantSelectionKind.SelectionRequired:
                var authorizeUrl = await BuildAuthorizeUrlAsync(cancellationToken: cancellationToken);
                return RedirectToPage("/Account/SelectTenant", new { returnUrl = authorizeUrl });
        }

        var tenant = selection.Tenant!;
        var principal = await principalFactory.CreateForUserAsync(
            user,
            tenant,
            request.GetScopes(),
            session.Properties?.IssuedUtc ?? timeProvider.GetUtcNow(),
            cancellationToken);

        // No explicit authorization entry: OpenIddict attaches an ad-hoc authorization per sign-in, so refresh-token
        // reuse detection revokes only this session's token chain.
        AuditLog.AuthorizationGranted(logger, request.ClientId, user.Id, tenant.Id);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ExchangeClientCredentialsAsync(request, cancellationToken);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await ExchangeUserGrantAsync(request, cancellationToken);
        }

        if (request.IsTokenExchangeGrantType())
        {
            return await ExchangeDelegationAsync(request, cancellationToken);
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    /// <summary>
    /// RFC 9449 §5: validates the DPoP proof sent to the token endpoint and returns the key thumbprint to bind the new
    /// tokens to. Clients registered with <c>dpop_bound_access_tokens</c>, and refresh tokens already bound to a key,
    /// must present a proof signed by that same key.
    /// </summary>
    private async Task<(string? Thumbprint, IActionResult? Error)> BindDPoPAsync(
        OpenIddictRequest request,
        string? boundThumbprint,
        CancellationToken cancellationToken)
    {
        var application = await applicationManager.FindByClientIdAsync(request.ClientId!, cancellationToken);
        var properties = application is null
            ? System.Collections.Immutable.ImmutableDictionary<string, JsonElement>.Empty
            : await applicationManager.GetPropertiesAsync(application, cancellationToken);
        var required = properties.TryGetValue(ClientProperties.DPoPBoundAccessTokens, out var flag) && flag.ValueKind == JsonValueKind.True;

        var proofs = Request.Headers[DPoPConstants.HeaderName];
        if (proofs.Count == 0)
        {
            return required || boundThumbprint is not null
                ? (null, Reject(request, DPoPConstants.InvalidProof, "A DPoP proof is required for this client."))
                : (null, null);
        }

        if (proofs.Count > 1)
        {
            return (null, Reject(request, DPoPConstants.InvalidProof, "Exactly one DPoP proof is allowed."));
        }

        var tokenEndpoint = new Uri(options.Value.Issuer!, "connect/token");
        var result = await dpopValidator.ValidateAsync(proofs[0], HttpMethods.Post, tokenEndpoint, accessToken: null, boundThumbprint, cancellationToken);
        if (!result.IsValid)
        {
            return (null, Reject(request, DPoPConstants.InvalidProof, result.Error!));
        }

        HttpContext.Items[DPoPBoundItem] = true;
        return (result.Thumbprint, null);
    }

    /// <summary>
    /// Binds the tokens being issued to a DPoP key: a private claim (no destination) keeps the binding inside the
    /// refresh token, and the thumbprint is handed to the sign-in pipeline, which writes <c>cnf.jkt</c> into the access
    /// token (OpenIddict reserves <c>cnf</c> on the principal for its own certificate binding).
    /// </summary>
    private void BindToKey(ClaimsPrincipal principal, string? thumbprint)
    {
        if (thumbprint is null)
        {
            return;
        }

        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(DPoPThumbprintClaim, thumbprint));
        HttpContext.Items[DPoPThumbprintItem] = thumbprint;
    }

    /// <summary>
    /// RFC 8693 token exchange (delegation). A resource server that received a user's access token (it must be one of that
    /// token's audiences) exchanges it for a token to call a downstream service on the user's behalf. The new token keeps
    /// the user as subject, records the caller in <c>act</c>, never gains lifetime, and carries only the requested scopes.
    /// </summary>
    private async Task<IActionResult> ExchangeDelegationAsync(OpenIddictRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.SubjectTokenType, TokenTypeIdentifiers.AccessToken, StringComparison.Ordinal))
        {
            return Reject(request, Errors.InvalidRequest, "Only access tokens can be exchanged.");
        }

        if (!string.IsNullOrEmpty(request.ActorToken))
        {
            return Reject(request, Errors.InvalidRequest, "Actor tokens are not supported; the authenticated client is the actor.");
        }

        var subjectToken = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("The subject token principal cannot be retrieved.");

        // Only an intended recipient of the user's token may act for the user (no token laundering by arbitrary clients).
        var audiences = subjectToken.GetAudiences();
        if (!audiences.Contains(request.ClientId!, StringComparer.Ordinal))
        {
            return Reject(request, Errors.InvalidGrant, "The client is not an audience of the subject token.");
        }

        var subject = subjectToken.GetClaim(Claims.Subject);
        var user = long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? await userManager.FindByIdAsync(subject!) : null;
        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            return Reject(request, Errors.InvalidGrant, "The subject token does not belong to an active user.");
        }

        var tenant = long.TryParse(subjectToken.GetClaim(EduEcoClaimTypes.TenantId), NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId)
            ? await tenantAccessResolver.FindAccessibleByIdAsync(user, tenantId, cancellationToken)
            : null;
        if (tenant is null)
        {
            return Reject(request, Errors.InvalidGrant, "Access to the tenant has been revoked.");
        }

        var scopes = request.GetScopes();
        if (scopes.IsEmpty)
        {
            return Reject(request, Errors.InvalidScope, "The downstream scope must be requested explicitly.");
        }

        var principal = await principalFactory.CreateForUserAsync(user, tenant, scopes, timeProvider.GetUtcNow(), cancellationToken);
        var identity = (ClaimsIdentity)principal.Identity!;

        var act = new Claim(ActorClaim, JsonSerializer.Serialize(new Dictionary<string, string> { [Claims.Subject] = request.ClientId! }), JsonClaimValueType);
        act.SetDestinations(Destinations.AccessToken);
        identity.AddClaim(act);

        // Delegated token never outlives the token it was derived from.
        if (subjectToken.GetExpirationDate() is { } subjectExpiry)
        {
            var remaining = subjectExpiry - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return Reject(request, Errors.InvalidGrant, "The subject token has expired.");
            }

            principal.SetAccessTokenLifetime(remaining < options.Value.AccessTokenLifetime ? remaining : options.Value.AccessTokenLifetime);
            principal.SetIssuedTokenLifetime(remaining < options.Value.AccessTokenLifetime ? remaining : options.Value.AccessTokenLifetime);
        }

        AuditLog.TokenExchanged(logger, request.ClientId, subject, string.Join(' ', principal.GetResources()));
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpGet("~/connect/endsession")]
    public async Task<IActionResult> EndSession()
    {
        // A valid id_token_hint (validated by OpenIddict) proves the request comes from the relying party the user
        // signed in to, so no confirmation is needed. Without it, ask the user (prevents cross-site logout).
        var hint = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (hint?.GetClaim(Claims.Subject) is { } hintSubject)
        {
            var current = await userManager.GetUserAsync(User);
            if (current is null || string.Equals(current.Id.ToString(CultureInfo.InvariantCulture), hintSubject, StringComparison.Ordinal))
            {
                await signInManager.SignOutAsync();
                logoutNotifier.Enqueue(hintSubject);
                AuditLog.SignedOut(logger, current?.Id);
                return SignOut(new AuthenticationProperties { RedirectUri = "/" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }
        }

        return Redirect("/Account/Logout" + Request.QueryString);
    }

    [HttpPost("~/connect/endsession")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndSessionConfirmed()
    {
        var user = await userManager.GetUserAsync(User);
        await signInManager.SignOutAsync();
        if (user is not null)
        {
            // Single logout: revoke this user's grants and notify relying parties (BFF deletes its sessions).
            logoutNotifier.Enqueue(user.Id.ToString(CultureInfo.InvariantCulture));
        }

        AuditLog.SignedOut(logger, user?.Id);

        // OpenIddict validates post_logout_redirect_uri against the client registration before redirecting.
        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> UserInfo()
    {
        var subject = User.GetClaim(Claims.Subject);
        var user = long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? await userManager.FindByIdAsync(subject!)
            : null;

        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            return Challenge(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token does not belong to an active user.",
                }),
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [Claims.Subject] = subject,
            [EduEcoClaimTypes.TenantId] = User.GetClaim(EduEcoClaimTypes.TenantId),
        };

        if (User.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = user.DisplayName ?? user.UserName;
            claims[Claims.PreferredUsername] = user.UserName;
        }

        if (User.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        return Ok(claims);
    }

    private async Task<IActionResult> ExchangeClientCredentialsAsync(OpenIddictRequest request, CancellationToken cancellationToken)
    {
        var application = await applicationManager.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("The client application cannot be found.");

        long? tenantId = null;
        var properties = await applicationManager.GetPropertiesAsync(application, cancellationToken);
        if (properties.TryGetValue(ClientProperties.TenantId, out var tenantElement) && tenantElement.ValueKind == JsonValueKind.Number)
        {
            tenantId = tenantElement.GetInt64();
            if (await tenantQueries.FindActiveTenantByIdAsync(tenantId.Value, cancellationToken) is null)
            {
                return Reject(request, Errors.UnauthorizedClient, "The client's tenant is inactive.");
            }
        }

        var (thumbprint, dpopError) = await BindDPoPAsync(request, boundThumbprint: null, cancellationToken);
        if (dpopError is not null)
        {
            return dpopError;
        }

        var principal = await principalFactory.CreateForClientAsync(
            request.ClientId!,
            await applicationManager.GetDisplayNameAsync(application, cancellationToken),
            tenantId,
            request.GetScopes(),
            cancellationToken);
        BindToKey(principal, thumbprint);

        AuditLog.TokenIssued(logger, request.ClientId, request.GrantType, request.ClientId, tenantId);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> ExchangeUserGrantAsync(OpenIddictRequest request, CancellationToken cancellationToken)
    {
        // Principal stored in the authorization code / refresh token (already validated by OpenIddict).
        var stored = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal
            ?? throw new InvalidOperationException("The authorization code or refresh token principal cannot be retrieved.");

        var subject = stored.GetClaim(Claims.Subject);
        var user = subject is null ? null : await userManager.FindByIdAsync(subject);
        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            return Reject(request, Errors.InvalidGrant, "The account is no longer allowed to sign in.");
        }

        if (!string.Equals(stored.GetClaim(TokenPrincipalFactory.SecurityStampClaim), await userManager.GetSecurityStampAsync(user), StringComparison.Ordinal))
        {
            return Reject(request, Errors.InvalidGrant, "The account credentials changed; sign in again.");
        }

        if (!long.TryParse(stored.GetClaim(TokenPrincipalFactory.SessionStartedClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var startedSeconds))
        {
            return Reject(request, Errors.InvalidGrant, "The grant is malformed.");
        }

        var sessionStarted = DateTimeOffset.FromUnixTimeSeconds(startedSeconds);
        if (request.IsRefreshTokenGrantType() && timeProvider.GetUtcNow() - sessionStarted > options.Value.RefreshTokenAbsoluteLifetime)
        {
            return Reject(request, Errors.InvalidGrant, "The session has expired; sign in again.");
        }

        var tenant = long.TryParse(stored.GetClaim(EduEcoClaimTypes.TenantId), NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId)
            ? await tenantAccessResolver.FindAccessibleByIdAsync(user, tenantId, cancellationToken)
            : null;
        if (tenant is null)
        {
            return Reject(request, Errors.InvalidGrant, "Access to the tenant has been revoked.");
        }

        // Refresh tokens issued to a DPoP key can only be redeemed with a proof from that key (stolen RT is useless).
        var boundThumbprint = stored.GetClaim(DPoPThumbprintClaim);
        var (thumbprint, dpopError) = await BindDPoPAsync(request, boundThumbprint, cancellationToken);
        if (dpopError is not null)
        {
            return dpopError;
        }

        // Rebuild claims from current state (roles/memberships may have changed) and keep the token chain.
        var principal = await principalFactory.CreateForUserAsync(user, tenant, stored.GetScopes(), sessionStarted, cancellationToken);
        principal.SetAuthorizationId(stored.GetAuthorizationId());
        BindToKey(principal, thumbprint);

        AuditLog.TokenIssued(logger, request.ClientId, request.GrantType, subject, tenant.Id);
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private ForbidResult Deny(OpenIddictRequest request, string error, string description)
    {
        AuditLog.AuthorizationDenied(logger, request.ClientId, error);
        return OidcForbid(error, description);
    }

    private ForbidResult Reject(OpenIddictRequest request, string error, string description)
    {
        AuditLog.TokenRejected(logger, request.ClientId, request.GrantType, description);
        return OidcForbid(error, description);
    }

    private ForbidResult OidcForbid(string error, string description) =>
        Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    /// <summary>Rebuilds the current authorization request as a local GET URL (POSTed requests are converted).</summary>
    private async Task<string> BuildAuthorizeUrlAsync(
        (string Name, string? Value)? replace = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<KeyValuePair<string, StringValues>> source = Request.HasFormContentType
            ? await Request.ReadFormAsync(cancellationToken)
            : Request.Query;

        var parameters = source
            .Where(p => replace is null || !string.Equals(p.Key, replace.Value.Name, StringComparison.Ordinal))
            .ToList();

        if (replace is { Value: not null } r)
        {
            parameters.Add(KeyValuePair.Create(r.Name, new StringValues(r.Value)));
        }

        return Request.PathBase + Request.Path + QueryString.Create(parameters);
    }
}
