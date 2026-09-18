using EduEco.Application.Abstractions.Persistence;
using EduEco.Core.Authorization;
using EduEco.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EduEco.Database.Seeders;

/// <summary>Demo users for local development. Refuses to run outside the Development environment.</summary>
internal sealed partial class DevUserSeeder(
    UserManager<ApplicationUser> userManager,
    ICommandRepository<UserTenantMembership> memberships,
    IQueryExecutor queryExecutor,
    IHostEnvironment environment,
    IOptions<SeedOptions> seedOptions,
    ILogger<DevUserSeeder> logger)
{
    /// <summary>Must match <c>Scripts/99_Dev/D0001__demo_tenant.sql</c>.</summary>
    public const string DemoTenantCode = "demo-school";

    private static readonly (string Email, string DisplayName, string Role, bool Global)[] Users =
    [
        ("platform.admin@eduEco.local", "Platform Admin", Roles.PlatformAdmin, true),
        ("tenant.admin@demo.eduEco.local", "Demo Tenant Admin", Roles.TenantAdmin, false),
        ("teacher@demo.eduEco.local", "Demo Teacher", Roles.Teacher, false),
        ("student@demo.eduEco.local", "Demo Student", Roles.Student, false),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Dev users can only be seeded in the Development environment.");
        }

        var password = seedOptions.Value.DevUsers.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Seed:DevUsers:Password is required (env/user-secrets).");
        }

        var demoTenantId = await queryExecutor.QuerySingleOrDefaultAsync<long?>(
            "SELECT Id FROM dbo.Tenants WHERE Code = @Code",
            new { Code = DemoTenantCode },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Demo tenant '{DemoTenantCode}' not found; 99_Dev scripts must run first.");

        foreach (var (email, displayName, roleName, global) in Users)
        {
            var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false);
            if (user is null)
            {
                user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName };
                EnsureSucceeded(await userManager.CreateAsync(user, password).ConfigureAwait(false), email);
                LogUserCreated(logger, email);
            }

            if (global)
            {
                if (!await userManager.IsInRoleAsync(user, roleName).ConfigureAwait(false))
                {
                    EnsureSucceeded(await userManager.AddToRoleAsync(user, roleName).ConfigureAwait(false), email);
                }

                continue;
            }

            var roleId = Roles.All.Single(r => r.Name == roleName).Id;
            var exists = await queryExecutor.ExecuteScalarAsync<bool>(
                """
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM auth.UserTenantMemberships
                    WHERE UserId = @UserId AND TenantId = @TenantId AND RoleId = @RoleId) THEN 1 ELSE 0 END
                """,
                new { UserId = user.Id, TenantId = demoTenantId, RoleId = roleId },
                cancellationToken).ConfigureAwait(false);

            if (!exists)
            {
                await memberships.InsertAsync(
                    new UserTenantMembership { TenantId = demoTenantId, UserId = user.Id, RoleId = roleId, IsDefault = true },
                    cancellationToken).ConfigureAwait(false);
                LogMembershipCreated(logger, email, roleName);
            }
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string email)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Seeding '{email}' failed: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dev user {Email} created")]
    private static partial void LogUserCreated(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dev membership {Email} -> {Role} created")]
    private static partial void LogMembershipCreated(ILogger logger, string email, string role);
}
