using EduEco.Core.Common;
using Microsoft.AspNetCore.Authorization;

namespace EduEco.Api.Security.Authorization;

/// <summary>Resource-based check (OWASP API1: BOLA): the resource must belong to the token's tenant.</summary>
public sealed class SameTenantRequirement : IAuthorizationRequirement
{
    public static readonly SameTenantRequirement Instance = new();
}

/// <summary>Minimal projection for resource checks on read models that are not <see cref="ITenantOwned"/> entities.</summary>
public interface ITenantResource
{
    long TenantId { get; }
}

internal sealed class SameTenantAuthorizationHandler : AuthorizationHandler<SameTenantRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        long? resourceTenant = context.Resource switch
        {
            ITenantOwned owned => owned.TenantId,
            ITenantResource resource => resource.TenantId,
            _ => null,
        };

        if (resourceTenant is not null && resourceTenant == context.User.GetTenantId())
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureCodes.ResourceOutsideTenant));
        }

        return Task.CompletedTask;
    }
}
