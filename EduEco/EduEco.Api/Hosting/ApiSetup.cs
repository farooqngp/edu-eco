using System.Threading.RateLimiting;
using EduEco.Api.Configuration;
using EduEco.Api.Security;
using EduEco.Api.Security.Authorization;
using EduEco.Application.Abstractions.Security;
using EduEco.Infrastructure.Security;
using EduEco.ServiceRegistry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;

namespace EduEco.Api.Hosting;

/// <summary>Composition root for the EduEco resource server.</summary>
internal static class ApiSetup
{
    public static WebApplicationBuilder AddEduEcoApi(this WebApplicationBuilder builder)
    {
        builder.AddEduEcoKeyVault();

        var services = builder.Services;
        var configuration = builder.Configuration;
        var environment = builder.Environment;

        var security = configuration.GetSection(ApiSecurityOptions.SectionName).Get<ApiSecurityOptions>() ?? new ApiSecurityOptions();
        Validate(security, environment);
        services.Configure<ApiSecurityOptions>(configuration.GetSection(ApiSecurityOptions.SectionName));

        // Data access + cached permission resolution. Caller/tenant come from the HTTP request, never "system".
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddEduEcoPersistence(configuration);
        services.AddEduEcoAuthorizationServices(configuration);
        services.AddEduEcoDPoP(configuration);
        services.AddSingleton<TokenIntrospectionService>();

        AddAuthentication(services, security, environment);
        AddAuthorization(services);
        AddRateLimiting(services, security);

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
        services.AddExceptionHandler<ApiExceptionHandler>();

        services.AddControllers();
        services.AddHealthChecks();
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer(new OAuthSecuritySchemeTransformer(security));
            options.AddOperationTransformer<AuthorizationOperationTransformer>();
        });

        return builder;
    }

    public static WebApplication UseEduEcoApi(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.Use(static (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            return next(context);
        });

        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseMiddleware<TenantStatusMiddleware>();
        app.UseAuthorization();

        app.MapControllers();
        app.MapHealthChecks("/health/live").AllowAnonymous().DisableRateLimiting();

        if (app.Environment.IsDevelopment())
        {
            var security = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiSecurityOptions>>().Value;
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference(options => options
                    .WithTitle("EduEco API")
                    .AddPreferredSecuritySchemes(OAuthSecuritySchemeTransformer.SchemeName)
                    .AddAuthorizationCodeFlow(OAuthSecuritySchemeTransformer.SchemeName, flow =>
                    {
                        flow.ClientId = security.ApiDocsClientId;
                        flow.Pkce = Pkce.Sha256;
                        flow.SelectedScopes = ["openid", EduEco.Core.Authorization.Scopes.ApiRead, EduEco.Core.Authorization.Scopes.ApiWrite];
                    }))
                .AllowAnonymous();
        }

        return app;
    }

    private static void Validate(ApiSecurityOptions options, IHostEnvironment environment)
    {
        if (options.Authority is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException("Authentication:Authority must be an absolute URI.");
        }

        if (!environment.IsDevelopment() && (options.AllowUntrustedBackchannelCertificate || !options.RequireHttpsMetadata))
        {
            throw new InvalidOperationException("Untrusted/non-HTTPS token metadata is only allowed in Development.");
        }
    }

    private static void AddAuthentication(IServiceCollection services, ApiSecurityOptions security, IHostEnvironment environment)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = security.Authority!.ToString();
                if (security.MetadataAddress is not null)
                {
                    options.MetadataAddress = security.MetadataAddress.ToString();
                }

                options.Audience = security.Audience;
                options.RequireHttpsMetadata = security.RequireHttpsMetadata;
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = environment.IsDevelopment();

                options.TokenValidationParameters.ValidIssuer = security.Authority.ToString();
                options.TokenValidationParameters.ValidAudience = security.Audience;
                options.TokenValidationParameters.ValidTypes = ["at+jwt"]; // RFC 9068: reject ID tokens and other JWTs
                options.TokenValidationParameters.ClockSkew = security.ClockSkew;
                options.TokenValidationParameters.NameClaimType = ClaimsPrincipalExtensions.SubjectClaim;
                options.TokenValidationParameters.RoleClaimType = ClaimsPrincipalExtensions.RoleClaim;

                if (security.AllowUntrustedBackchannelCertificate && environment.IsDevelopment())
                {
                    options.BackchannelHttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                }

                options.SaveToken = true; // needed by [RequireActiveToken] (introspection)

                options.Events = new JwtBearerEvents
                {
                    // RFC 9449 §7.1: accept "Authorization: DPoP <token>" in addition to Bearer.
                    OnMessageReceived = context =>
                    {
                        string authorization = context.Request.Headers.Authorization.ToString();
                        if (authorization.StartsWith(DPoPConstants.AuthorizationScheme + " ", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Token = authorization[(DPoPConstants.AuthorizationScheme.Length + 1)..].Trim();
                            context.HttpContext.Items[DPoPSchemeItem] = true;
                        }

                        return Task.CompletedTask;
                    },

                    // RFC 9449 §7: a token bound to a key (cnf.jkt) is only usable with a fresh proof from that key.
                    OnTokenValidated = async context =>
                    {
                        var usedDPoPScheme = context.HttpContext.Items.ContainsKey(DPoPSchemeItem);
                        var thumbprint = DPoPProofValidator.ReadThumbprint(context.Principal?.FindFirst(DPoPConstants.ConfirmationClaim)?.Value);

                        if (thumbprint is null)
                        {
                            if (usedDPoPScheme)
                            {
                                Reject(context, "The access token is not DPoP-bound.");
                            }

                            return;
                        }

                        if (!usedDPoPScheme)
                        {
                            Reject(context, "DPoP-bound access tokens must use the DPoP authorization scheme.");
                            return;
                        }

                        var request = context.HttpContext.Request;
                        var proofs = request.Headers[DPoPConstants.HeaderName];
                        var requestUri = new Uri($"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}");
                        var token = (context.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)?.EncodedToken;

                        var result = proofs.Count != 1
                            ? DPoPValidationResult.Failure("Exactly one DPoP proof is required.")
                            : await context.HttpContext.RequestServices.GetRequiredService<DPoPProofValidator>()
                                .ValidateAsync(proofs[0], request.Method, requestUri, token, thumbprint, context.HttpContext.RequestAborted);

                        if (!result.IsValid)
                        {
                            context.HttpContext.Items[DPoPErrorItem] = true;
                            Reject(context, result.Error!);
                        }
                    },

                    // RFC 6750 / RFC 9449 challenge header + RFC 9457 body. Validation details are only exposed in Development.
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();

                        var dpop = context.HttpContext.Items.ContainsKey(DPoPSchemeItem);
                        var parameters = new List<string>();
                        if (context.AuthenticateFailure is not null)
                        {
                            parameters.Add(context.HttpContext.Items.ContainsKey(DPoPErrorItem)
                                ? $"error=\"{DPoPConstants.InvalidProof}\""
                                : "error=\"invalid_token\"");
                            if (!string.IsNullOrEmpty(context.ErrorDescription))
                            {
                                parameters.Add($"error_description=\"{context.ErrorDescription.Replace("\"", "'", StringComparison.Ordinal)}\"");
                            }
                        }

                        if (dpop)
                        {
                            parameters.Add("algs=\"ES256 ES384 PS256 RS256\"");
                        }

                        // RFC 6750 §3: auth-params are comma-separated after the scheme (no comma after the scheme).
                        var scheme = dpop ? DPoPConstants.AuthorizationScheme : "Bearer";
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = parameters.Count == 0 ? scheme : scheme + " " + string.Join(", ", parameters);

                        var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                        await problemDetails.WriteAsync(new ProblemDetailsContext
                        {
                            HttpContext = context.HttpContext,
                            ProblemDetails = new ProblemDetails
                            {
                                Status = StatusCodes.Status401Unauthorized,
                                Title = "Unauthorized",
                                Detail = context.AuthenticateFailure is null
                                    ? "A bearer access token is required."
                                    : "The access token is invalid or expired.",
                                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                                Extensions = { ["code"] = context.AuthenticateFailure is null ? "token_required" : "invalid_token" },
                            },
                        });
                    },
                };
            });
    }

    private const string DPoPSchemeItem = "EduEco.DPoPScheme";
    private const string DPoPErrorItem = "EduEco.DPoPError";

    private static void Reject(TokenValidatedContext context, string reason)
    {
        context.Fail(reason);
    }

    private static void AddAuthorization(IServiceCollection services)
    {
        var authenticatedOnly = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();

        // Secure by default: endpoints without [Authorize]/[HasPermission] still require a valid token.
        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(authenticatedOnly)
            .SetFallbackPolicy(authenticatedOnly);

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, ActiveTokenAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, SameTenantAuthorizationHandler>();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();
    }

    private static void AddRateLimiting(IServiceCollection services, ApiSecurityOptions security)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var key = context.User.GetClientId() is { } clientId
                    ? $"client:{clientId}:{context.User.GetSubject()}"
                    : $"ip:{context.Connection.RemoteIpAddress}";

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = security.PermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
    }
}
