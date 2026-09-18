using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Security.Authorization;

/// <summary>
/// Turns authorization failures into RFC 9457 problem details. Scope failures also carry the RFC 6750
/// <c>WWW-Authenticate: Bearer error="insufficient_scope"</c> header so clients can request the missing scope.
/// </summary>
internal sealed class ProblemDetailsAuthorizationResultHandler(IProblemDetailsService problemDetails) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden)
        {
            // Success → next; Challenge → JwtBearer OnChallenge writes the 401 problem.
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var reasons = authorizeResult.AuthorizationFailure?.FailureReasons.Select(r => r.Message).ToList() ?? [];

        // A revoked token is an authentication failure (RFC 6750 invalid_token), not missing rights.
        if (reasons.Contains(AuthorizationFailureCodes.TokenRevoked))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\", error_description=\"The access token has been revoked\"";
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthorized",
                    Detail = "The access token has been revoked. Sign in again.",
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                    Extensions = { ["code"] = AuthorizationFailureCodes.TokenRevoked },
                },
            });
            return;
        }

        var scope =reasons.FirstOrDefault(r => r.StartsWith(AuthorizationFailureCodes.InsufficientScopePrefix, StringComparison.Ordinal));

        string code;
        string detail;
        if (scope is not null)
        {
            var missing = scope[AuthorizationFailureCodes.InsufficientScopePrefix.Length..];
            context.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{missing}\"";
            code = "insufficient_scope";
            detail = $"The access token lacks the '{missing}' scope.";
        }
        else if (reasons.Contains(AuthorizationFailureCodes.TenantRequired))
        {
            code = AuthorizationFailureCodes.TenantRequired;
            detail = "This operation requires a tenant-bound access token.";
        }
        else if (reasons.Contains(AuthorizationFailureCodes.ServiceClientNotAllowed))
        {
            code = AuthorizationFailureCodes.ServiceClientNotAllowed;
            detail = "This operation requires an end-user token.";
        }
        else
        {
            code = AuthorizationFailureCodes.PermissionDenied;
            detail = "You do not have permission to perform this operation.";
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = detail,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                Extensions = { ["code"] = code },
            },
        });
    }
}
