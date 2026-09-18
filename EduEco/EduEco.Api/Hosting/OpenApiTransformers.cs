using EduEco.Api.Configuration;
using EduEco.Api.Security.Authorization;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EduEco.Api.Hosting;

/// <summary>Documents the OAuth 2.0 authorization code (PKCE) flow against EduEco.Identity.</summary>
internal sealed class OAuthSecuritySchemeTransformer(ApiSecurityOptions security) : IOpenApiDocumentTransformer
{
    public const string SchemeName = "oauth2";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var authority = security.Authority!;
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "EduEco Identity (OpenID Connect). Authorization code + PKCE; access tokens are RFC 9068 JWTs.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri(authority, "connect/authorize"),
                    TokenUrl = new Uri(authority, "connect/token"),
                    RefreshUrl = new Uri(authority, "connect/token"),
                    Scopes = new Dictionary<string, string>
                    {
                        ["openid"] = "OpenID Connect sign-in",
                        [Scopes.ApiRead] = "Read EduEco data",
                        [Scopes.ApiWrite] = "Modify EduEco data",
                    },
                },
            },
        };

        return Task.CompletedTask;
    }
}

/// <summary>Adds security requirements (with required scopes/permissions) and 401/403 responses to protected operations.</summary>
internal sealed class AuthorizationOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        var permissions = metadata.OfType<HasPermissionAttribute>().Select(a => Permissions.Find(a.Permission)!).ToList();
        var scopes = permissions.Select(p => p.RequiredScope).Distinct(StringComparer.Ordinal).ToList();

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(OAuthSecuritySchemeTransformer.SchemeName, context.Document)] = scopes,
        });

        if (permissions.Count > 0)
        {
            var list = string.Join(", ", permissions.Select(p => $"`{p.Name}`"));
            operation.Description = $"{operation.Description}\n\n**Requires permission:** {list}".Trim();
        }

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing, invalid or expired access token." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Insufficient scope, missing tenant, or permission denied." });
        return Task.CompletedTask;
    }
}
