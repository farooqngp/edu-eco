using EduEco.Core.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EduEco.Infrastructure.Persistence.Auth;

/// <summary>
/// EF Core context used only by ASP.NET Core Identity and OpenIddict stores.
/// No EF migrations: the <c>auth</c> schema is owned by DbUp scripts in <c>EduEco.Database</c>.
/// Regenerate reference DDL with <c>dotnet run --project EduEco.Database -- generate-auth-ddl</c> when upgrading Identity/OpenIddict.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, long>(options)
{
    public const string Schema = "auth";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(Schema);
        base.OnModelCreating(builder);

        builder.UseOpenIddict<long>();

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(200);
        });

        builder.Entity<ApplicationRole>(role =>
        {
            role.Property(r => r.Description).HasMaxLength(400);
        });
    }
}
