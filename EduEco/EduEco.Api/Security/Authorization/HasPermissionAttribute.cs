using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace EduEco.Api.Security.Authorization;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>
/// Declares the permission an endpoint requires. Evaluated by <see cref="PermissionAuthorizationHandler"/>:
/// scope (client delegation) AND permission (user rights in the token's tenant), or scope-only for eligible service clients.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class HasPermissionAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public HasPermissionAttribute(string permission)
    {
        Permission = Permissions.Find(permission)?.Name
            ?? throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme;
    }

    public string Permission { get; }

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return new PermissionRequirement(Permission);
    }
}

public static class PermissionEndpointExtensions
{
    /// <summary>Minimal API equivalent of <see cref="HasPermissionAttribute"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new HasPermissionAttribute(permission));
}
