using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using EduEco.Core.Identity;
using EduEco.Identity.Configuration;
using EduEco.Identity.Services;
using EduEco.Infrastructure.Security;
using EduEco.ServiceRegistry;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using ApiScopes = EduEco.Core.Authorization.Scopes;
using EduEcoClaimTypes = EduEco.Core.Authorization.EduEcoClaimTypes;

namespace EduEco.Identity.Hosting;

/// <summary>Composition root for the OpenID Connect authorization server.</summary>
internal static class IdentityServerSetup
{
    public static WebApplicationBuilder AddEduEcoIdentityServer(this WebApplicationBuilder builder)
    {
        // Key Vault first: its secrets override appsettings/environment (connection strings, SMTP password, ...).
        builder.AddEduEcoKeyVault();

        var services = builder.Services;
        var configuration = builder.Configuration;
        var environment = builder.Environment;
        var certificates = CertificateLoader.Create(configuration);

        var options = configuration.GetSection(IdentityServerOptions.SectionName).Get<IdentityServerOptions>() ?? new IdentityServerOptions();
        Validate(options, environment, KeyVaultExtensions.HasAzureKeyRing(configuration));
        services.Configure<IdentityServerOptions>(configuration.GetSection(IdentityServerOptions.SectionName));

        // Data access (Dapper) + Identity/OpenIddict stores (EF Core, schema owned by DbUp).
        services.AddEduEcoPersistence(configuration);
        services.AddEduEcoAuthStores(configuration)
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.TryAddScoped<IPasskeyHandler<ApplicationUser>, PasskeyHandler<ApplicationUser>>();
        services.AddEduEcoSystemContext("identity");
        services.AddEduEcoReplayCache(configuration); // DPoP + client assertion jti (cluster-wide with Redis)
        services.AddEduEcoDPoP(configuration);

        // Password reset / email confirmation links are single-purpose and short-lived.
        services.Configure<DataProtectionTokenProviderOptions>(tokens => tokens.TokenLifespan = TimeSpan.FromMinutes(30));
        services.AddOptions<Email.SmtpOptions>().Bind(configuration.GetSection(Email.SmtpOptions.SectionName));
        services.TryAddSingleton<IEmailSender<ApplicationUser>, Email.SmtpEmailSender>();

        services.AddSingleton<Logout.BackchannelLogoutNotifier>();
        services.AddHostedService<Logout.BackchannelLogoutWorker>();
        var backchannelLogout = services.AddHttpClient(Logout.BackchannelLogoutWorker.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
        if (options.AllowUntrustedBackchannelLogoutCertificates && environment.IsDevelopment())
        {
            backchannelLogout.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        }

        AddCookiesAndDataProtection(services, configuration, options, environment, certificates);
        AddOpenIddictServer(services, options, environment, certificates);
        AddRateLimiting(services, options);

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            forwarded.ForwardLimit = 1;
            forwarded.KnownProxies.Clear();
            foreach (var proxy in options.KnownProxies)
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });

        services.AddHsts(hsts =>
        {
            hsts.MaxAge = TimeSpan.FromDays(365);
            hsts.IncludeSubDomains = true;
        });

        services.AddControllers();
        services.AddRazorPages(pages =>
        {
            pages.Conventions.AuthorizeFolder("/Account/Manage");
            pages.Conventions.AuthorizePage("/Account/SelectTenant");
        });
        services.AddHealthChecks();

        services.AddScoped<TokenPrincipalFactory>();
        services.AddScoped<TenantAccessResolver>();
        if (options.TokenPruningEnabled)
        {
            services.AddHostedService<TokenPruningService>();
        }

        return builder;
    }

    public static WebApplication UseEduEcoIdentityServer(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityServerOptions>>().Value;

        app.UseForwardedHeaders();

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        if (options.RequireHttps)
        {
            app.UseHttpsRedirection();
        }

        // form-action has no default-src fallback: without it an injected form could post credentials anywhere.
        var formAction = string.Join(' ', new[] { "'self'" }.Concat(options.FormActionOrigins.Select(o => new Uri(o).GetLeftPart(UriPartial.Authority))));
        var contentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; "
            + $"form-action {formAction}; frame-ancestors 'none'";

        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            // WebAuthn (passkeys) stays available to this origin only.
            headers["Permissions-Policy"] =
                "camera=(), microphone=(), geolocation=(), payment=(), usb=(), publickey-credentials-get=(self), publickey-credentials-create=(self)";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.ContentSecurityPolicy = contentSecurityPolicy;
            return next(context);
        });

        app.UseStatusCodePagesWithReExecute("/Error");
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapControllers();
        app.MapRazorPages();
        app.MapHealthChecks("/health/live").AllowAnonymous().DisableRateLimiting();

        return app;
    }

    private static void Validate(IdentityServerOptions options, IHostEnvironment environment, bool hasAzureKeyRing)
    {
        if (options.Issuer is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException("IdentityServer:Issuer must be an absolute URI.");
        }

        if (environment.IsProduction())
        {
            if (options.CredentialMode != CredentialMode.Certificate)
            {
                throw new InvalidOperationException("IdentityServer:CredentialMode must be 'Certificate' in Production.");
            }

            if (!options.RequireHttps || options.Issuer.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("HTTPS is mandatory in Production.");
            }

            if (string.IsNullOrWhiteSpace(options.DataProtectionKeysPath) && !hasAzureKeyRing)
            {
                throw new InvalidOperationException("IdentityServer:DataProtectionKeysPath or DataProtection:BlobUri is required in Production.");
            }
        }

        if (options.AllowUntrustedBackchannelLogoutCertificates && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("AllowUntrustedBackchannelLogoutCertificates is only allowed in Development.");
        }

        if (options.CredentialMode == CredentialMode.Certificate
            && (options.SigningCertificates.Count == 0 || options.EncryptionCertificates.Count == 0))
        {
            throw new InvalidOperationException("Certificate mode requires IdentityServer:SigningCertificates and EncryptionCertificates.");
        }

        if (options.RefreshTokenAbsoluteLifetime < options.RefreshTokenLifetime)
        {
            throw new InvalidOperationException("RefreshTokenAbsoluteLifetime must be >= RefreshTokenLifetime.");
        }
    }

    private static void AddCookiesAndDataProtection(
        IServiceCollection services, IConfiguration configuration, IdentityServerOptions options, IHostEnvironment environment, CertificateLoader certificates)
    {
        var dataProtection = services.AddDataProtection().SetApplicationName("EduEco.Identity");
        if (!string.IsNullOrWhiteSpace(options.DataProtectionKeysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysPath));
        }

        // Cloud: key ring in Blob Storage wrapped by a Key Vault key. Otherwise encrypt it with the encryption certificates.
        var protectedByKeyVault = dataProtection.UseAzureKeyRing(configuration);
        if (!protectedByKeyVault && options.CredentialMode == CredentialMode.Certificate)
        {
            // Encrypt the key ring at rest. All configured encryption certificates can decrypt (supports rotation).
            var encryption = options.EncryptionCertificates.Select(c => Load(certificates, c)).ToArray();
            dataProtection.ProtectKeysWithCertificate(encryption[0]).UnprotectKeysWithAnyCertificate(encryption);
        }

        services.AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = IdentityConstants.ApplicationScheme;
                authentication.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        // __Host- prefix: Secure, Path=/, no Domain → cannot be set or overwritten by sibling sub-domains.
        // SameSite=Lax is required: OIDC authorization requests arrive as cross-site top-level navigations.
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = "__Host-EduEco.Session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.ExpireTimeSpan = options.SessionLifetime;
            cookie.SlidingExpiration = false;
            cookie.LoginPath = "/Account/Login";
            cookie.LogoutPath = "/Account/Logout";
            cookie.AccessDeniedPath = "/Account/AccessDenied";
        });

        foreach (var (scheme, name) in new[]
                 {
                     (IdentityConstants.ExternalScheme, "__Host-EduEco.External"),
                     (IdentityConstants.TwoFactorUserIdScheme, "__Host-EduEco.2fa"),
                     (IdentityConstants.TwoFactorRememberMeScheme, "__Host-EduEco.2faRemember"),
                 })
        {
            services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(scheme, cookie =>
            {
                cookie.Cookie.Name = name;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
            });
        }

        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "__Host-EduEco.Antiforgery";
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.Configure<IdentityPasskeyOptions>(passkeys =>
        {
            passkeys.ServerDomain = options.Issuer!.Host;
            passkeys.UserVerificationRequirement = "required";
            passkeys.ResidentKeyRequirement = "preferred";
        });

        if (environment.IsProduction())
        {
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
        }
    }

    private static void AddOpenIddictServer(
        IServiceCollection services, IdentityServerOptions options, IHostEnvironment environment, CertificateLoader certificates)
    {
        services.AddOpenIddict()
            .AddServer(server =>
            {
                server.SetIssuer(options.Issuer!);

                server.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetEndSessionEndpointUris("connect/endsession")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetRevocationEndpointUris("connect/revoke")
                    .SetIntrospectionEndpointUris("connect/introspect")
                    .SetPushedAuthorizationEndpointUris("connect/par");

                // OAuth 2.1 / RFC 9700: code + PKCE (S256) for users, client credentials for services,
                // token exchange (RFC 8693) for delegation. Implicit, hybrid, password (ROPC) and device flows stay off.
                server.AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowClientCredentialsFlow()
                    .AllowRefreshTokenFlow()
                    .AllowTokenExchangeFlow();

                server.Configure(o => o.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                server.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess,
                    ApiScopes.ApiRead, ApiScopes.ApiWrite, ApiScopes.ApiSync, ApiScopes.ReportingRead);
                server.RegisterClaims(Claims.Subject, Claims.Name, Claims.PreferredUsername, Claims.Email, Claims.EmailVerified,
                    Claims.Role, EduEcoClaimTypes.TenantId, DPoPConstants.ConfirmationClaim, Controllers.AuthorizationController.ActorClaim);

                // RFC 9449 / OIDC Back-Channel Logout metadata, and token_type=DPoP for DPoP-bound access tokens.
                server.AddEventHandler<OpenIddictServerEvents.ApplyConfigurationResponseContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        context.Response["dpop_signing_alg_values_supported"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "ES256", "ES384", "PS256", "RS256" });
                        context.Response["backchannel_logout_supported"] = true;
                        context.Response["backchannel_logout_session_supported"] = false;
                        return default;
                    })
                    .SetOrder(int.MinValue + 100_000));

                server.AddEventHandler(ClientAssertionReplayHandler.Descriptor);

                // OpenIddict only understands certificate-bound confirmations (cnf.x5t#S256) and throws on DPoP's cnf.jkt when
                // it re-reads a token (e.g. the subject token of a token exchange). DPoP proofs are verified where they apply:
                // by resource servers for every request and by the token endpoint for refresh. A subject token presented for
                // exchange comes from a resource server that already verified the proof and does not hold the user's key.
                server.AddEventHandler<OpenIddictServerEvents.ValidateTokenContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        if (context.Principal?.GetClaim(OpenIddictConstants.Claims.Confirmation) is { Length: > 0 } confirmation
                            && DPoPProofValidator.ReadThumbprint(confirmation) is not null
                            && !confirmation.Contains("x5t#S256", StringComparison.Ordinal))
                        {
                            context.DisableProofOfPossessionValidation = true;
                        }

                        return default;
                    })
                    .SetOrder(OpenIddictServerHandlers.Protection.ValidateProofOfPossession.Descriptor.Order - 1)
                    .SetType(OpenIddictServerHandlerType.Custom));

                // RFC 9449 §6: write cnf.jkt into the access token after OpenIddict has prepared its principal.
                server.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        if (context.AccessTokenPrincipal is { } accessToken
                            && context.Transaction.GetHttpRequest()?.HttpContext.Items[Controllers.AuthorizationController.DPoPThumbprintItem] is string thumbprint)
                        {
                            accessToken.SetClaim(DPoPConstants.ConfirmationClaim,
                                System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string> { [DPoPConstants.ThumbprintMember] = thumbprint }));
                        }

                        return default;
                    })
                    .SetOrder(OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order + 1));

                server.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(handler => handler
                    .UseInlineHandler(context =>
                    {
                        if (context.Transaction.GetHttpRequest()?.HttpContext.Items[Controllers.AuthorizationController.DPoPBoundItem] is true
                            && !string.IsNullOrEmpty(context.Response.AccessToken))
                        {
                            context.Response.TokenType = DPoPConstants.AuthorizationScheme;
                        }

                        return default;
                    })
                    .SetOrder(int.MinValue + 100_000));

                server.SetAccessTokenLifetime(options.AccessTokenLifetime)
                    .SetIdentityTokenLifetime(options.IdentityTokenLifetime)
                    .SetAuthorizationCodeLifetime(options.AuthorizationCodeLifetime)
                    .SetRefreshTokenLifetime(options.RefreshTokenLifetime)
                    .SetRefreshTokenReuseLeeway(options.RefreshTokenReuseLeeway);

                // Signed (JWS) access tokens so resource servers validate locally via JWKS (RFC 9068, typ=at+jwt).
                server.DisableAccessTokenEncryption();

                switch (options.CredentialMode)
                {
                    case CredentialMode.Certificate:
                        server.AddSigningCertificates(options.SigningCertificates.Select(c => Load(certificates, c)));
                        server.AddEncryptionCertificates(options.EncryptionCertificates.Select(c => Load(certificates, c)));
                        break;
                    case CredentialMode.Development:
                        server.AddDevelopmentSigningCertificate().AddDevelopmentEncryptionCertificate();
                        break;
                    case CredentialMode.Ephemeral:
                        server.AddEphemeralSigningKey().AddEphemeralEncryptionKey();
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported credential mode {options.CredentialMode}.");
                }

                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();

                if (!options.RequireHttps && !environment.IsProduction())
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            });
    }

    private static void AddRateLimiting(IServiceCollection services, IdentityServerOptions options)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!HttpMethods.IsPost(context.Request.Method))
                {
                    return RateLimitPartition.GetNoLimiter(string.Empty);
                }

                var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var path = context.Request.Path;

                if (path.StartsWithSegments("/connect/token", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/connect/par", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/connect/introspect", StringComparison.OrdinalIgnoreCase))
                {
                    return FixedWindow($"token:{client}", options.RateLimits.TokenPermitsPerMinute);
                }

                // Credential-guessing and enumeration surfaces share the login budget.
                if (path.StartsWithSegments("/Account/Login", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/LoginWith2fa", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/LoginWithRecoveryCode", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/Reauthenticate", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/ForgotPassword", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/ResetPassword", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/ResendEmailConfirmation", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/Account/Register", StringComparison.OrdinalIgnoreCase))
                {
                    return FixedWindow($"login:{client}", options.RateLimits.LoginPermitsPerMinute);
                }

                return RateLimitPartition.GetNoLimiter(string.Empty);
            });
        });

        static RateLimitPartition<string> FixedWindow(string key, int permits) =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }

    /// <summary>PKCS#12 file (Docker volume) or Key Vault certificate (cloud).</summary>
    private static X509Certificate2 Load(CertificateLoader certificates, CertificateOptions certificate) =>
        certificates.Load(certificate.Path, certificate.Password, certificate.KeyVaultName);
}
