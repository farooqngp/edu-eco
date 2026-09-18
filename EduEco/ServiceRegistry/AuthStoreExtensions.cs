using EduEco.Core.Identity;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Identity;
using EduEco.Infrastructure.Persistence.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EduEco.ServiceRegistry;

public static class AuthStoreExtensions
{
    /// <summary>
    /// ASP.NET Core Identity + OpenIddict core stores over <see cref="AuthDbContext"/> (EF Core, schema owned by DbUp).
    /// Hosts add their own Identity options / OpenIddict server or validation on top.
    /// </summary>
    public static IdentityBuilder AddEduEcoAuthStores(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured.");
        }

        services.AddDbContext<AuthDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

        services.AddOpenIddict()
            .AddCore(options => options
                .UseEntityFrameworkCore()
                .UseDbContext<AuthDbContext>()
                .ReplaceDefaultEntities<long>());

        return services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                // Version3 adds AspNetUserPasskeys (WebAuthn). Schema is owned by DbUp (V0006).
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                options.SignIn.RequireConfirmedAccount = true; // Enforced by ActiveUserConfirmation (IsActive + EmailConfirmed).
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddUserConfirmation<ActiveUserConfirmation>();
    }
}
