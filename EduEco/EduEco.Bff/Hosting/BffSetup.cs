using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using EduEco.Bff.Configuration;
using EduEco.Bff.Endpoints;
using EduEco.Bff.Proxy;
using EduEco.Bff.Sessions;
using EduEco.Bff.Tokens;
using EduEco.ServiceRegistry;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace EduEco.Bff.Hosting;

/// <summary>Composition root for the Backend-for-Frontend (IETF draft-ietf-oauth-browser-based-apps, "BFF" pattern).</summary>
internal static class BffSetup
{
    /// <summary>Claims kept from the ID token in the server-side ticket (everything else is dropped).</summary>
    private static readonly HashSet<string> RetainedClaims =
        new(StringComparer.Ordinal) { "sub", "name", "preferred_username", "email", "email_verified", Core.Authorization.EduEcoClaimTypes.TenantId };

    public static WebApplicationBuilder AddEduEcoBff(this WebApplicationBuilder builder)
    {
        builder.AddEduEcoKeyVault();

        var services = builder.Services;
        var environment = builder.Environment;
        var options = builder.Configuration.GetSection(BffOptions.SectionName).Get<BffOptions>() ?? new BffOptions();
        Validate(options, environment, KeyVaultExtensions.HasAzureKeyRing(builder.Configuration));
        services.Configure<BffOptions>(builder.Configuration.GetSection(BffOptions.SectionName));

        services.AddEduEcoPersistence(builder.Configuration);
        services.AddEduEcoSystemContext("bff");
        services.AddEduEcoReplayCache(builder.Configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SqlSessionStore>();
        services.AddSingleton<BffClientAuthentication>();
        services.AddSingleton<SqlRefreshLock>();
        services.AddSingleton<UserTokenService>();
        services.AddHostedService<SessionCleanupService>();

        var dataProtection = services.AddDataProtection().SetApplicationName("EduEco.Bff");
        if (!string.IsNullOrWhiteSpace(options.DataProtectionKeysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysPath));
        }

        dataProtection.UseAzureKeyRing(builder.Configuration);

        AddAuthentication(services, options, environment);
        AddReverseProxy(services, options, environment);

        services.AddProblemDetails();
        services.AddHealthChecks();
        return builder;
    }

    public static WebApplication UseEduEcoBff(this WebApplication app)
    {
        app.UseExceptionHandler();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.Use(static (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
            return next(context);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // The SPA shell must be revalidated so a deployment is picked up immediately.
            OnPrepareResponse = static context =>
            {
                if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            },
        });
        app.UseRouting();
        app.UseAuthentication();

        app.MapBffEndpoints();
        app.MapBackchannelLogout();
        app.MapReverseProxy(proxy => proxy.UseMiddleware<ApiProxyGuardMiddleware>());
        app.MapHealthChecks("/health/live");

        return app;
    }

    private static void Validate(BffOptions options, IHostEnvironment environment, bool hasAzureKeyRing)
    {
        if (options.Authority is not { IsAbsoluteUri: true } || options.ApiBaseAddress is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException("Bff:Authority and Bff:ApiBaseAddress must be absolute URIs.");
        }

        var usesPrivateKeyJwt = options.ClientAssertion.IsConfigured;
        if (!usesPrivateKeyJwt && string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException("Bff:ClientAssertion (CertificatePath or KeyVaultName, private_key_jwt) or Bff:ClientSecret is required.");
        }

        if (environment.IsProduction() && !usesPrivateKeyJwt)
        {
            throw new InvalidOperationException("Production requires private_key_jwt (Bff:ClientAssertion); client secrets are not allowed.");
        }

        if (!environment.IsDevelopment() && (options.AllowUntrustedCertificates || !options.RequireHttpsMetadata))
        {
            throw new InvalidOperationException("Untrusted certificates / non-HTTPS metadata are only allowed in Development.");
        }

        if (environment.IsProduction() && string.IsNullOrWhiteSpace(options.DataProtectionKeysPath) && !hasAzureKeyRing)
        {
            throw new InvalidOperationException("Bff:DataProtectionKeysPath or DataProtection:BlobUri is required in Production.");
        }
    }

    private static void AddAuthentication(IServiceCollection services, BffOptions options, IHostEnvironment environment)
    {
        services.AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = CookieSchemes.Session;
                authentication.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieSchemes.Session, cookie =>
            {
                // __Host-: Secure, Path=/, no Domain. SameSite=Strict: the SPA's same-origin fetches carry it; cross-site
                // requests never do (CSRF). The cookie holds only an opaque session key (see SqlSessionStore).
                cookie.Cookie.Name = "__Host-EduEco.Bff";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.ExpireTimeSpan = options.SessionLifetime;
                cookie.SlidingExpiration = false;

                // APIs answer 401/403; they never redirect an XHR to the Identity server.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                oidc.Authority = options.Authority!.ToString();
                oidc.ClientId = options.ClientId;
                // private_key_jwt replaces the secret (see OnAuthorizationCodeReceived / OnPushAuthorization).
                oidc.ClientSecret = options.ClientAssertion.IsConfigured ? null : options.ClientSecret;
                oidc.ResponseType = OpenIdConnectResponseType.Code;
                oidc.ResponseMode = OpenIdConnectResponseMode.Query;
                oidc.UsePkce = true;
                oidc.PushedAuthorizationBehavior = options.UsePushedAuthorization
                    ? PushedAuthorizationBehavior.Require
                    : PushedAuthorizationBehavior.Disable;
                oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;
                oidc.MapInboundClaims = false;
                oidc.SaveTokens = true; // captured by SqlSessionStore and removed from the ticket
                oidc.GetClaimsFromUserInfoEndpoint = false;
                oidc.UseTokenLifetime = false;
                oidc.DisableTelemetry = true;
                oidc.CallbackPath = "/signin-oidc";
                oidc.SignedOutCallbackPath = "/signout-callback-oidc";
                oidc.SignedOutRedirectUri = "/";
                oidc.TokenValidationParameters.NameClaimType = "name";
                oidc.TokenValidationParameters.RoleClaimType = "role";

                oidc.Scope.Clear();
                foreach (var scope in options.Scopes)
                {
                    oidc.Scope.Add(scope);
                }

                oidc.CorrelationCookie.Name = "__Secure-EduEco.Bff.Correlation.";
                oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
                oidc.NonceCookie.Name = "__Secure-EduEco.Bff.Nonce.";
                oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                oidc.NonceCookie.SameSite = SameSiteMode.Lax;

                if (options.AllowUntrustedCertificates && environment.IsDevelopment())
                {
                    oidc.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                }

                oidc.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = context =>
                    {
                        if (context.Properties.Items.TryGetValue(BffEndpoints.TenantItem, out var tenant) && tenant is not null)
                        {
                            context.ProtocolMessage.SetParameter(BffEndpoints.TenantItem, tenant);
                        }

                        return Task.CompletedTask;
                    },
                    OnPushAuthorization = async context =>
                    {
                        var clientAuthentication = context.HttpContext.RequestServices.GetRequiredService<BffClientAuthentication>();
                        if (clientAuthentication.UsesPrivateKeyJwt)
                        {
                            var configuration = await context.Options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
                            clientAuthentication.Apply(context.ProtocolMessage, configuration.Issuer);
                            context.HandleClientAuthentication();
                        }
                    },
                    OnAuthorizationCodeReceived = async context =>
                    {
                        var clientAuthentication = context.HttpContext.RequestServices.GetRequiredService<BffClientAuthentication>();
                        if (clientAuthentication.UsesPrivateKeyJwt && context.TokenEndpointRequest is { } tokenRequest)
                        {
                            var configuration = await context.Options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
                            clientAuthentication.Apply(tokenRequest, configuration.Issuer);
                        }
                    },
                    OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        if (context.Properties?.Items.TryGetValue(BffEndpoints.IdTokenHintItem, out var hint) == true)
                        {
                            context.ProtocolMessage.IdTokenHint = hint;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        // Keep a minimal principal and attach the public session id.
                        var source = context.Principal!;
                        var identity = new ClaimsIdentity(
                            source.Claims.Where(c => RetainedClaims.Contains(c.Type)),
                            source.Identity!.AuthenticationType,
                            "name",
                            "role");
                        identity.AddClaim(new Claim(SqlSessionStore.SessionIdClaim, RandomNumberGenerator.GetHexString(32, lowercase: true)));
                        context.Principal = new ClaimsPrincipal(identity);
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        // e.g. access_denied (no tenant access) or cancelled login: back to the SPA, no stack trace.
                        context.HandleResponse();
                        context.Response.Redirect("/?login_error=1");
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddOptions<CookieAuthenticationOptions>(CookieSchemes.Session)
            .Configure<SqlSessionStore>((cookie, store) => cookie.SessionStore = store);
    }

    private static void AddReverseProxy(IServiceCollection services, BffOptions options, IHostEnvironment environment)
    {
        services.AddReverseProxy()
            .LoadFromMemory(
                [
                    new RouteConfig
                    {
                        RouteId = "api",
                        ClusterId = "api",
                        Match = new RouteMatch { Path = "/api/{**catch-all}" },
                    },
                ],
                [
                    new ClusterConfig
                    {
                        ClusterId = "api",
                        Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
                        {
                            ["api"] = new() { Address = options.ApiBaseAddress!.ToString() },
                        },
                        HttpClient = new HttpClientConfig
                        {
                            DangerousAcceptAnyServerCertificate = options.AllowUntrustedCertificates && environment.IsDevelopment(),
                        },
                    },
                ])
            .AddTransforms(transforms =>
            {
                // Browser credentials never reach the API; the BFF substitutes the user's bearer token.
                transforms.AddRequestHeaderRemove("Cookie");
                transforms.AddRequestHeaderRemove("Authorization");
                transforms.AddRequestHeaderRemove(BffEndpoints.CsrfHeaderName);
                transforms.AddRequestTransform(context =>
                {
                    if (context.HttpContext.Items[ApiProxyGuardMiddleware.AccessTokenItem] is string token)
                    {
                        context.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }

                    return ValueTask.CompletedTask;
                });
                transforms.AddResponseHeaderRemove("Set-Cookie");
            });
    }
}
