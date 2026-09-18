using EduEco.Application.Abstractions.Security;
using EduEco.Core.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace EduEco.Api.Security.Authorization;

/// <summary>Failure reason codes surfaced as RFC 9457 problem details / RFC 6750 WWW-Authenticate.</summary>
public static class AuthorizationFailureCodes
{
    public const string InsufficientScopePrefix = "insufficient_scope:";
    public const string TenantRequired = "tenant_required";
    public const string ServiceClientNotAllowed = "service_client_not_allowed";
    public const string PermissionDenied = "permission_denied";
    public const string ResourceOutsideTenant = "resource_outside_tenant";
    public const string TokenRevoked = "token_revoked";
}

internal sealed partial class PermissionAuthorizationHandler(
    IPermissionService permissionService,
    ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var definition = Permissions.Find(requirement.Permission)!;

        // 1. Delegation ceiling: the client must have been granted the scope, whatever the user's rights.
        if (!user.HasScope(definition.RequiredScope))
        {
            Deny(context, AuthorizationFailureCodes.InsufficientScopePrefix + definition.RequiredScope, requirement);
            return;
        }

        var tenantId = user.GetTenantId();
        if (definition.TenantScoped && tenantId is null)
        {
            Deny(context, AuthorizationFailureCodes.TenantRequired, requirement);
            return;
        }

        // 2a. Service clients: scope-only, restricted to permissions explicitly eligible for automation.
        if (user.IsServiceClient())
        {
            if (definition.AllowServiceClients)
            {
                context.Succeed(requirement);
            }
            else
            {
                Deny(context, AuthorizationFailureCodes.ServiceClientNotAllowed, requirement);
            }

            return;
        }

        // 2b. Users: permission resolved server-side for (user, tenant). Global roles (PlatformAdmin) apply in any tenant.
        var userId = user.GetUserId();
        if (userId is null || tenantId is null)
        {
            Deny(context, AuthorizationFailureCodes.PermissionDenied, requirement);
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        if (await permissionService.HasPermissionAsync(userId.Value, tenantId.Value, definition.Name, cancellationToken))
        {
            context.Succeed(requirement);
        }
        else
        {
            Deny(context, AuthorizationFailureCodes.PermissionDenied, requirement);
        }
    }

    private void Deny(AuthorizationHandlerContext context, string code, PermissionRequirement requirement)
    {
        LogDenied(logger, requirement.Permission, code, context.User.GetSubject(), context.User.GetClientId());
        context.Fail(new AuthorizationFailureReason(this, code));
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Warning,
        Message = "AUDIT authorization denied: permission {Permission}, reason {Reason}, subject {Subject}, client {ClientId}")]
    private static partial void LogDenied(ILogger logger, string permission, string reason, string? subject, string? clientId);
}
