using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Core.Authorization;
using EduEco.Core.Common;
using EduEco.Core.Identity;
using EduEco.Core.Tenants;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Persistence.Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EduEco.Infrastructure.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class CommandRepositoryTests(SqlServerFixture fixture)
{
    private static readonly ITenantContext CrossTenant = new TestTenantContext(null, isCrossTenant: true);

    [Fact]
    public async Task Tenant_insert_get_update_stamps_audit_and_row_version()
    {
        await using var session = NewSession();
        var repository = Repository<Tenant>(session, CrossTenant);

        var tenant = NewTenant();
        await repository.InsertAsync(tenant, TestContext.Current.CancellationToken);

        tenant.Id.ShouldBeGreaterThan(0);
        tenant.CreatedBy.ShouldBe("test:integration");
        tenant.RowVersion.ShouldNotBeEmpty();

        var loaded = (await repository.GetByIdAsync(tenant.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        loaded.Name.ShouldBe(tenant.Name);

        var originalVersion = loaded.RowVersion;
        loaded.Name = "Renamed";
        (await repository.UpdateAsync(loaded, TestContext.Current.CancellationToken)).ShouldBeTrue();

        loaded.RowVersion.ShouldNotBe(originalVersion);
        loaded.UpdatedBy.ShouldBe("test:integration");
        (await repository.GetByIdAsync(tenant.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull().Name.ShouldBe("Renamed");
    }

    [Fact]
    public async Task Stale_row_version_throws_concurrency_exception()
    {
        await using var session = NewSession();
        var repository = Repository<Tenant>(session, CrossTenant);

        var tenant = NewTenant();
        await repository.InsertAsync(tenant, TestContext.Current.CancellationToken);

        var first = (await repository.GetByIdAsync(tenant.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var second = (await repository.GetByIdAsync(tenant.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        first.Name = "Writer one";
        (await repository.UpdateAsync(first, TestContext.Current.CancellationToken)).ShouldBeTrue();

        second.Name = "Writer two";
        await Should.ThrowAsync<ConcurrencyException>(() => repository.UpdateAsync(second, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_of_missing_row_returns_false()
    {
        await using var session = NewSession();
        var tenant = NewTenant();
        tenant.Id = long.MaxValue;

        (await Repository<Tenant>(session, CrossTenant).UpdateAsync(tenant, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Rollback_discards_changes()
    {
        var tenant = NewTenant();

        await using (var session = NewSession())
        {
            await session.BeginAsync(cancellationToken: TestContext.Current.CancellationToken);
            await Repository<Tenant>(session, CrossTenant).InsertAsync(tenant, TestContext.Current.CancellationToken);
            await session.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var verify = NewSession();
        (await Repository<Tenant>(verify, CrossTenant).GetByIdAsync(tenant.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Tenant_owned_rows_are_isolated_between_tenants()
    {
        var (tenantA, tenantB, userId) = await ArrangeTwoTenantsAndUserAsync();
        var roleId = Roles.All.Single(r => r.Name == Roles.Teacher).Id;

        await using var session = NewSession();
        var repoA = Repository<UserTenantMembership>(session, new TestTenantContext(tenantA));
        var repoB = Repository<UserTenantMembership>(session, new TestTenantContext(tenantB));

        var membership = new UserTenantMembership { UserId = userId, RoleId = roleId };
        await repoA.InsertAsync(membership, TestContext.Current.CancellationToken);
        membership.TenantId.ShouldBe(tenantA, "tenant id is stamped from the tenant context");

        (await repoB.GetByIdAsync(membership.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await repoB.UpdateAsync(membership, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await repoB.DeleteAsync(membership.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();

        (await repoA.GetByIdAsync(membership.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await repoA.DeleteAsync(membership.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task Cross_tenant_insert_from_tenant_context_is_rejected()
    {
        var (tenantA, tenantB, userId) = await ArrangeTwoTenantsAndUserAsync();

        await using var session = NewSession();
        var repoA = Repository<UserTenantMembership>(session, new TestTenantContext(tenantA));

        await Should.ThrowAsync<InvalidOperationException>(() => repoA.InsertAsync(
            new UserTenantMembership { TenantId = tenantB, UserId = userId, RoleId = Roles.All[0].Id },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tenant_owned_access_without_tenant_context_is_rejected()
    {
        await using var session = NewSession();
        var repository = Repository<UserTenantMembership>(session, new TestTenantContext(null));

        await Should.ThrowAsync<InvalidOperationException>(() => repository.GetByIdAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Paged_query_returns_page_and_total()
    {
        var prefix = $"pg-{Guid.NewGuid():N}"[..12];
        await using (var session = NewSession())
        {
            var repository = Repository<Tenant>(session, CrossTenant);
            await repository.InsertRangeAsync(
                [.. Enumerable.Range(1, 5).Select(i => new Tenant { Code = $"{prefix}-{i}", Name = $"Paged {i}" })],
                TestContext.Current.CancellationToken);
        }

        var executor = fixture.Services.GetRequiredService<IQueryExecutor>();
        var page = await executor.QueryPagedAsync<Tenant>(
            "SELECT * FROM dbo.Tenants WHERE Code LIKE @Prefix ORDER BY Code",
            "SELECT COUNT(*) FROM dbo.Tenants WHERE Code LIKE @Prefix",
            new { Prefix = prefix + "%" },
            new PageRequest(pageNumber: 2, pageSize: 2),
            TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(5);
        page.TotalPages.ShouldBe(3);
        page.Items.Select(t => t.Code).ShouldBe([$"{prefix}-3", $"{prefix}-4"]);
    }

    internal async Task<(long TenantA, long TenantB, long UserId)> ArrangeTwoTenantsAndUserAsync()
    {
        Tenant a = NewTenant(), b = NewTenant();
        await using (var session = NewSession())
        {
            await Repository<Tenant>(session, CrossTenant).InsertRangeAsync([a, b], TestContext.Current.CancellationToken);
        }

        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@it.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "IT user" };
        (await users.CreateAsync(user, "Integration!Test9Pw")).Succeeded.ShouldBeTrue();

        return (a.Id, b.Id, user.Id);
    }

    internal static Tenant NewTenant() => new() { Code = $"t-{Guid.NewGuid():N}"[..20], Name = "Integration School" };

    private DapperSession NewSession() => new(fixture.Services.GetRequiredService<IDbConnectionFactory>());

    private DapperCommandRepository<T> Repository<T>(DapperSession session, ITenantContext tenantContext)
        where T : class, IEntity =>
        new(session, tenantContext, new TestCurrentUser(), TimeProvider.System,
            fixture.Services.GetRequiredService<IOptions<DatabaseOptions>>());
}
