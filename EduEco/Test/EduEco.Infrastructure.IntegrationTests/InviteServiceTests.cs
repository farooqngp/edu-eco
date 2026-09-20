using System.Security.Cryptography;
using System.Text;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Common;
using EduEco.Application.Invites;
using EduEco.Core.Authorization;
using EduEco.Core.Common;
using EduEco.Core.Identity;
using EduEco.Core.Tenants;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Persistence.Dapper;
using EduEco.Infrastructure.Queries;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EduEco.Infrastructure.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class InviteServiceTests(SqlServerFixture fixture)
{
    private static readonly ITenantContext CrossTenant = new TestTenantContext(null, isCrossTenant: true);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issue_then_validate_returns_the_tenant_and_role()
    {
        await using var session = NewSession();
        var tenant = await NewTenantAsync(session);
        var emails = new RecordingInviteEmailSender();
        var service = NewService(session, new TestTenantContext(tenant), emails);

        var issued = await service.IssueAsync(new CreateInviteCommand(Roles.Teacher, DateTimeOffset.UtcNow.AddDays(1), "invitee@it.local"), Ct);
        issued.Succeeded.ShouldBeTrue(issued.Detail);
        issued.Value!.Code.ShouldNotBeNullOrEmpty();

        emails.Sent.ShouldBe([("invitee@it.local", issued.Value.Code, Roles.Teacher)]);

        var redemption = await service.ValidateAsync(issued.Value.Code, Ct);
        redemption.Succeeded.ShouldBeTrue(redemption.Detail);
        redemption.Value!.TenantId.ShouldBe(tenant);
        redemption.Value.RoleName.ShouldBe(Roles.Teacher);
    }

    [Fact]
    public async Task Consume_marks_the_invite_used_and_a_second_redemption_is_rejected()
    {
        await using var session = NewSession();
        var tenant = await NewTenantAsync(session);
        var service = NewService(session, new TestTenantContext(tenant));

        var issued = (await service.IssueAsync(new CreateInviteCommand(Roles.Student, DateTimeOffset.UtcNow.AddDays(1), "invitee@it.local"), Ct)).Value!;
        var validated = (await service.ValidateAsync(issued.Code, Ct)).Value!;
        var userId = await NewUserAsync();

        (await service.ConsumeAsync(validated.InviteId, userId, Ct)).Succeeded.ShouldBeTrue();

        var revalidated = await service.ValidateAsync(issued.Code, Ct);
        revalidated.Succeeded.ShouldBeFalse();
        revalidated.Error.ShouldBe(ResultError.Conflict);

        (await service.ConsumeAsync(validated.InviteId, await NewUserAsync(), Ct)).Error.ShouldBe(ResultError.Conflict);
    }

    [Fact]
    public async Task Unknown_code_is_rejected()
    {
        await using var session = NewSession();
        var result = await NewService(session, CrossTenant).ValidateAsync("0000000000000000dead", Ct);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(ResultError.NotFound);
    }

    [Fact]
    public async Task Expired_invite_is_rejected()
    {
        await using var session = NewSession();
        var tenant = await NewTenantAsync(session);
        var invite = new TenantInvite
        {
            CodeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("expired-code-value"))),
            RoleId = Roles.All.Single(r => r.Name == Roles.Student).Id,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await Repository<TenantInvite>(session, new TestTenantContext(tenant)).InsertAsync(invite, Ct);

        var result = await NewService(session, CrossTenant).ValidateAsync("expired-code-value", Ct);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(ResultError.Validation);
    }

    [Fact]
    public async Task Issuing_with_a_non_assignable_role_is_rejected()
    {
        await using var session = NewSession();
        var tenant = await NewTenantAsync(session);

        var result = await NewService(session, new TestTenantContext(tenant))
            .IssueAsync(new CreateInviteCommand("PlatformAdmin", DateTimeOffset.UtcNow.AddDays(1), "invitee@it.local"), Ct);

        result.Succeeded.ShouldBeFalse();
        result.Error.ShouldBe(ResultError.Validation);
    }

    private async Task<long> NewUserAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@it.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Invitee" };
        (await users.CreateAsync(user, "Integration!Test9Pw")).Succeeded.ShouldBeTrue();
        return user.Id;
    }

    private async Task<long> NewTenantAsync(DapperSession session)
    {
        var tenant = CommandRepositoryTests.NewTenant();
        await Repository<Tenant>(session, CrossTenant).InsertAsync(tenant, Ct);
        return tenant.Id;
    }

    private InviteService NewService(DapperSession session, ITenantContext tenantContext, IInviteEmailSender? emailSender = null) =>
        new(
            Repository<TenantInvite>(session, tenantContext),
            new InviteQueries(fixture.Services.GetRequiredService<IQueryExecutor>()),
            emailSender ?? new RecordingInviteEmailSender(),
            TimeProvider.System);

    private sealed class RecordingInviteEmailSender : IInviteEmailSender
    {
        public List<(string Email, string Code, string RoleName)> Sent { get; } = [];

        public Task SendInviteAsync(string email, string code, string roleName, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        {
            Sent.Add((email, code, roleName));
            return Task.CompletedTask;
        }
    }

    private DapperCommandRepository<T> Repository<T>(DapperSession session, ITenantContext tenantContext)
        where T : class, IEntity =>
        new(session, tenantContext, new TestCurrentUser(), TimeProvider.System, fixture.Services.GetRequiredService<IOptions<DatabaseOptions>>());

    private DapperSession NewSession() => new(fixture.Services.GetRequiredService<IDbConnectionFactory>());
}
