using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Authorization;
using EduEco.Core.Authorization;
using EduEco.Core.Identity;
using EduEco.Core.Tenants;
using EduEco.Database.Seeders;
using EduEco.Infrastructure.Persistence.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Shouldly;

namespace EduEco.Infrastructure.IntegrationTests;

/// <summary>Schema-drift guard: EF-based Identity/OpenIddict stores must work against the DbUp-built schema.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthStoreTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Every_auth_entity_set_is_queryable()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        foreach (var entityType in context.Model.GetEntityTypes().Where(e => !e.IsOwned()))
        {
            // Materialising one row per mapped table validates table + column names/types.
            var set = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(context, null)!;

            await Should.NotThrowAsync(() => set.Take(1).ToListAsync(TestContext.Current.CancellationToken),
                $"entity {entityType.ClrType.Name} ({entityType.GetTableName()})");
        }
    }

    [Fact]
    public async Task Client_seeder_is_idempotent_and_hashes_secret()
    {
        for (var run = 0; run < 2; run++)
        {
            await using var seedScope = fixture.Services.CreateAsyncScope();
            await seedScope.ServiceProvider.GetRequiredService<OpenIddictClientSeeder>().SeedAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = fixture.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = (await applications.FindByClientIdAsync("it-confidential", TestContext.Current.CancellationToken)).ShouldNotBeNull();

        (await applications.ValidateClientSecretAsync(client, SqlServerFixture.BffSecret, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await applications.HasPermissionAsync(client, OpenIddictConstants.Permissions.GrantTypes.ClientCredentials, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        var apiRead = (await scopes.FindByNameAsync(Scopes.ApiRead, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await scopes.GetResourcesAsync(apiRead, TestContext.Current.CancellationToken)).ShouldContain(Resources.Api);
    }

    [Fact]
    public async Task Permission_query_combines_tenant_membership_and_global_roles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var tenantRepository = services.GetRequiredService<ICommandRepository<Tenant>>();
        Tenant tenantA = CommandRepositoryTests.NewTenant(), tenantB = CommandRepositoryTests.NewTenant();
        await tenantRepository.InsertRangeAsync([tenantA, tenantB], cancellationToken);

        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var teacher = await CreateUserAsync(users);
        var admin = await CreateUserAsync(users);
        (await users.AddToRoleAsync(admin, Roles.PlatformAdmin)).Succeeded.ShouldBeTrue();

        await services.GetRequiredService<ICommandRepository<UserTenantMembership>>().InsertAsync(
            new UserTenantMembership { TenantId = tenantA.Id, UserId = teacher.Id, RoleId = Roles.All.Single(r => r.Name == Roles.Teacher).Id },
            cancellationToken);

        var queries = services.GetRequiredService<IPermissionQueries>();

        (await queries.GetPermissionNamesAsync(teacher.Id, tenantA.Id, cancellationToken))
            .ShouldBe(Roles.All.Single(r => r.Name == Roles.Teacher).Permissions, ignoreOrder: true);
        (await queries.GetPermissionNamesAsync(teacher.Id, tenantB.Id, cancellationToken)).ShouldBeEmpty();
        (await queries.GetPermissionNamesAsync(admin.Id, tenantB.Id, cancellationToken))
            .ShouldBe(Permissions.All.Select(p => p.Name), ignoreOrder: true);

        teacher.IsActive = false;
        (await users.UpdateAsync(teacher)).Succeeded.ShouldBeTrue();
        (await queries.GetPermissionNamesAsync(teacher.Id, tenantA.Id, cancellationToken)).ShouldBeEmpty("inactive users have no permissions");
    }

    private static async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> users)
    {
        var email = $"{Guid.NewGuid():N}@it.local";
        var user = new ApplicationUser { UserName = email, Email = email };
        (await users.CreateAsync(user, "Integration!Test9Pw")).Succeeded.ShouldBeTrue();
        return user;
    }
}
