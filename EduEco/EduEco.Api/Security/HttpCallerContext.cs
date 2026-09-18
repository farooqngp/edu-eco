using System.Globalization;
using EduEco.Application.Abstractions.Security;

namespace EduEco.Api.Security;

/// <summary>Caller identity from the validated access token of the current request.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public long? UserId => Principal?.GetUserId();

    public string? ClientId => Principal?.GetClientId();

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public bool IsServiceClient => Principal?.IsServiceClient() == true;

    public string AuditName =>
        IsServiceClient ? $"client:{ClientId}"
        : UserId is { } id ? string.Create(CultureInfo.InvariantCulture, $"user:{id}")
        : "anonymous";
}

/// <summary>
/// Tenant from the token's <c>tenant_id</c> claim. Never cross-tenant: repositories reject tenant-owned access without it.
/// </summary>
internal sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public long? TenantId => accessor.HttpContext?.User.GetTenantId();

    public bool IsCrossTenant => false;
}
