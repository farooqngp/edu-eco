using System.Text.Json;
using EduEco.Application.Tenants;
using EduEco.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using ApiResources = EduEco.Core.Authorization.Resources;
using ApiScopes = EduEco.Core.Authorization.Scopes;
using ClientProperties = EduEco.Core.Authorization.ClientProperties;

namespace EduEco.Database.Seeders;

/// <summary>
/// Upserts API scopes and OAuth client registrations. Done in code (not SQL) because OpenIddict hashes client secrets
/// and serialises permissions/requirements.
/// </summary>
internal sealed partial class OpenIddictClientSeeder(
    IOpenIddictApplicationManager applicationManager,
    IOpenIddictScopeManager scopeManager,
    ITenantQueries tenantQueries,
    IOptions<SeedOptions> seedOptions,
    ILogger<OpenIddictClientSeeder> logger,
    IHostEnvironment? environment = null)
{
    private static readonly (string Name, string DisplayName, string Resource)[] ScopeDefinitions =
    [
        (ApiScopes.ApiRead, "Read EduEco data", ApiResources.Api),
        (ApiScopes.ApiWrite, "Modify EduEco data", ApiResources.Api),
        (ApiScopes.ApiSync, "Bulk synchronisation (service-to-service)", ApiResources.Api),
        (ApiScopes.ReportingRead, "Read reports (delegated via token exchange)", ApiResources.Reporting),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, displayName, resource) in ScopeDefinitions)
        {
            var descriptor = new OpenIddictScopeDescriptor { Name = name, DisplayName = displayName };
            descriptor.Resources.Add(resource);

            var existing = await scopeManager.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await scopeManager.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await scopeManager.UpdateAsync(existing, descriptor, cancellationToken).ConfigureAwait(false);
            }

            LogScopeUpserted(logger, name);
        }

        // Shared secrets are a development convenience only; production confidential clients must use private_key_jwt.
        var allowClientSecrets = environment?.IsProduction() != true;

        foreach (var (clientId, seed) in seedOptions.Value.Clients)
        {
            long? tenantId = null;
            if (!string.IsNullOrWhiteSpace(seed.TenantCode))
            {
                tenantId = (await tenantQueries.FindActiveTenantByCodeAsync(seed.TenantCode, cancellationToken).ConfigureAwait(false))?.Id
                    ?? throw new InvalidOperationException($"Client '{clientId}': tenant '{seed.TenantCode}' not found or inactive.");
            }

            if (seed.PublicKeyCertificatePath is { Length: > 0 } certificatePath && !Path.IsPathRooted(certificatePath) && environment is not null)
            {
                // Relative paths are relative to the migrator's content root (dotnet run), not the working directory.
                seed.PublicKeyCertificatePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, certificatePath));
            }

            var descriptor = BuildDescriptor(clientId, seed, tenantId, allowClientSecrets);

            var existing = await applicationManager.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await applicationManager.CreateAsync(descriptor, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await applicationManager.UpdateAsync(existing, descriptor, cancellationToken).ConfigureAwait(false);
            }

            LogClientUpserted(logger, clientId, seed.ClientType, descriptor.JsonWebKeySet is null ? "client_secret" : "private_key_jwt");
        }
    }

    internal static OpenIddictApplicationDescriptor BuildDescriptor(
        string clientId,
        ClientSeed seed,
        long? tenantId = null,
        bool allowClientSecrets = true)
    {
        ArgumentNullException.ThrowIfNull(seed);

        if (tenantId is not null && !seed.GrantTypes.SequenceEqual([GrantTypes.ClientCredentials]))
        {
            throw new InvalidOperationException($"Client '{clientId}': TenantCode is only valid for client_credentials-only clients.");
        }

        var isConfidential = string.Equals(seed.ClientType, ClientTypes.Confidential, StringComparison.Ordinal);
        if (!isConfidential && !string.Equals(seed.ClientType, ClientTypes.Public, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Client '{clientId}': ClientType must be 'confidential' or 'public'.");
        }

        var hasSecret = !string.IsNullOrWhiteSpace(seed.ClientSecret);
        var hasPublicKey = !string.IsNullOrWhiteSpace(seed.PublicKeyCertificatePath);

        if (isConfidential && !hasSecret && !hasPublicKey)
        {
            throw new InvalidOperationException(
                $"Client '{clientId}': confidential clients require PublicKeyCertificatePath (private_key_jwt) or, outside production, ClientSecret.");
        }

        if (hasSecret && !allowClientSecrets)
        {
            throw new InvalidOperationException($"Client '{clientId}': client secrets are not allowed in production; use private_key_jwt.");
        }

        if (!isConfidential && (hasSecret || hasPublicKey))
        {
            throw new InvalidOperationException($"Client '{clientId}': public clients must not have credentials.");
        }

        if (seed.GrantTypes.Count == 0 && !seed.AllowIntrospection)
        {
            throw new InvalidOperationException($"Client '{clientId}': at least one grant type is required.");
        }

        if (seed.AllowIntrospection && !isConfidential)
        {
            throw new InvalidOperationException($"Client '{clientId}': only confidential clients may introspect tokens.");
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = hasSecret ? seed.ClientSecret : null,
            ClientType = seed.ClientType,
            ApplicationType = seed.ApplicationType,
            DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? clientId : seed.DisplayName,
            ConsentType = ConsentTypes.Implicit, // First-party clients only.
        };

        if (hasPublicKey)
        {
            using var certificate = KeyMaterial.LoadPublicCertificate(seed.PublicKeyCertificatePath!, seed.PublicKeyCertificatePassword);
            descriptor.JsonWebKeySet = new JsonWebKeySet { Keys = { KeyMaterial.ToPublicJsonWebKey(certificate) } };
        }

        if (tenantId is not null)
        {
            descriptor.Properties[ClientProperties.TenantId] = JsonSerializer.SerializeToElement(tenantId.Value);
        }

        if (!string.IsNullOrWhiteSpace(seed.BackchannelLogoutUri))
        {
            descriptor.Properties[ClientProperties.BackchannelLogoutUri] =
                JsonSerializer.SerializeToElement(new Uri(seed.BackchannelLogoutUri, UriKind.Absolute).AbsoluteUri);
        }

        if (seed.RequireDPoP)
        {
            descriptor.Properties[ClientProperties.DPoPBoundAccessTokens] = JsonSerializer.SerializeToElement(true);
        }

        if (seed.AllowIntrospection)
        {
            descriptor.Permissions.Add(Permissions.Endpoints.Introspection);
        }

        if (seed.GrantTypes.Count > 0)
        {
            descriptor.Permissions.Add(Permissions.Endpoints.Token);
            descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
        }

        foreach (var grantType in seed.GrantTypes)
        {
            switch (grantType)
            {
                case GrantTypes.AuthorizationCode:
                    descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
                    descriptor.Permissions.Add(Permissions.Endpoints.PushedAuthorization);
                    descriptor.Permissions.Add(Permissions.Endpoints.EndSession);
                    descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
                    descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
                    descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
                    if (seed.RequirePushedAuthorizationRequests)
                    {
                        descriptor.Requirements.Add(Requirements.Features.PushedAuthorizationRequests);
                    }

                    break;
                case GrantTypes.RefreshToken:
                    descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
                    break;
                case GrantTypes.ClientCredentials when isConfidential:
                    descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
                    break;
                case GrantTypes.TokenExchange when isConfidential:
                    descriptor.Permissions.Add(Permissions.GrantTypes.TokenExchange);
                    break;
                default:
                    throw new InvalidOperationException($"Client '{clientId}': grant type '{grantType}' is not allowed.");
            }
        }

        // openid / offline_access are governed by the flow itself; everything else needs an explicit scope permission.
        foreach (var scope in seed.Scopes.Where(s => s is not (OpenIddictConstants.Scopes.OpenId or OpenIddictConstants.Scopes.OfflineAccess)))
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        foreach (var resource in seed.Resources)
        {
            descriptor.Permissions.Add(Permissions.Prefixes.Resource + resource);
        }

        foreach (var uri in seed.RedirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        foreach (var uri in seed.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(new Uri(uri, UriKind.Absolute));
        }

        return descriptor;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scope {Scope} upserted")]
    private static partial void LogScopeUpserted(ILogger logger, string scope);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {ClientId} ({ClientType}, {AuthMethod}) upserted")]
    private static partial void LogClientUpserted(ILogger logger, string clientId, string clientType, string authMethod);
}
