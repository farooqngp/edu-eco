using EduEco.Database.Seeders;
using Shouldly;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EduEco.Database.Tests;

public sealed class OpenIddictClientSeederTests
{
    [Fact]
    public void Authorization_code_client_requires_pkce_and_gets_scope_permissions()
    {
        var descriptor = OpenIddictClientSeeder.BuildDescriptor("spa", new ClientSeed
        {
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Native,
            GrantTypes = { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken },
            Scopes = { Scopes.OpenId, Scopes.OfflineAccess, "api.read" },
            RedirectUris = { "com.example.app:/callback" },
        });

        descriptor.Requirements.ShouldContain(Requirements.Features.ProofKeyForCodeExchange);
        descriptor.Permissions.ShouldContain(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.ShouldContain(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.ShouldContain(Permissions.Prefixes.Scope + "api.read");
        descriptor.Permissions.ShouldNotContain(Permissions.Prefixes.Scope + Scopes.OpenId);
        descriptor.ClientSecret.ShouldBeNull();
    }

    [Fact]
    public void Confidential_client_without_secret_is_rejected() =>
        Should.Throw<InvalidOperationException>(() => OpenIddictClientSeeder.BuildDescriptor("svc", new ClientSeed
        {
            ClientType = ClientTypes.Confidential,
            GrantTypes = { GrantTypes.ClientCredentials },
        }));

    [Fact]
    public void Public_client_with_secret_is_rejected() =>
        Should.Throw<InvalidOperationException>(() => OpenIddictClientSeeder.BuildDescriptor("spa", new ClientSeed
        {
            ClientType = ClientTypes.Public,
            ClientSecret = "should-not-be-here",
            GrantTypes = { GrantTypes.AuthorizationCode },
        }));

    [Fact]
    public void Tenant_binding_is_stored_as_client_property_for_service_clients()
    {
        var descriptor = OpenIddictClientSeeder.BuildDescriptor("svc", new ClientSeed
        {
            ClientType = ClientTypes.Confidential,
            ClientSecret = "service-secret-0123456789abcdefghij",
            GrantTypes = { GrantTypes.ClientCredentials },
            Scopes = { "api.read" },
        }, tenantId: 42);

        descriptor.Properties[EduEco.Core.Authorization.ClientProperties.TenantId].GetInt64().ShouldBe(42);
    }

    [Fact]
    public void Tenant_binding_is_rejected_for_interactive_clients() =>
        Should.Throw<InvalidOperationException>(() => OpenIddictClientSeeder.BuildDescriptor("web", new ClientSeed
        {
            ClientType = ClientTypes.Confidential,
            ClientSecret = "web-secret-0123456789abcdefghij",
            GrantTypes = { GrantTypes.AuthorizationCode },
        }, tenantId: 42));

    [Theory]
    [InlineData(GrantTypes.ClientCredentials)] // public clients cannot authenticate
    [InlineData(GrantTypes.Password)] // ROPC is prohibited (RFC 9700)
    [InlineData(GrantTypes.Implicit)] // implicit is prohibited (RFC 9700)
    public void Disallowed_grants_are_rejected_for_public_clients(string grantType) =>
        Should.Throw<InvalidOperationException>(() => OpenIddictClientSeeder.BuildDescriptor("spa", new ClientSeed
        {
            ClientType = ClientTypes.Public,
            GrantTypes = { grantType },
        }));
}
