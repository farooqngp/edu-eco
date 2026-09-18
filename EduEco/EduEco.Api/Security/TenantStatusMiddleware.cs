using EduEco.Application.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Security;

/// <summary>
/// Runs after authentication: rejects malformed tenant claims and tokens for deactivated tenants
/// (a token stays cryptographically valid until expiry; tenant status is checked per request, cached briefly).
/// </summary>
internal sealed class TenantStatusMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantStatusService tenantStatus, IProblemDetailsService problemDetails)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true && user.HasTenantClaim())
        {
            var tenantId = user.GetTenantId();
            if (tenantId is null)
            {
                await WriteAsync(context, problemDetails, StatusCodes.Status401Unauthorized, "invalid_token", "The tenant claim is malformed.");
                return;
            }

            if (!await tenantStatus.IsActiveAsync(tenantId.Value, context.RequestAborted))
            {
                await WriteAsync(context, problemDetails, StatusCodes.Status403Forbidden, "tenant_inactive", "The tenant is inactive.");
                return;
            }
        }

        await next(context);
    }

    private static async Task WriteAsync(HttpContext context, IProblemDetailsService problemDetails, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        }

        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Forbidden",
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }
}
