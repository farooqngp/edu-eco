using Dapper;
using EduEco.Application.Abstractions.Security;

namespace EduEco.Infrastructure.Persistence.Dapper;

/// <summary>Adds the mandatory <c>@TenantId</c> parameter for tenant-owned read queries.</summary>
public static class TenantParameters
{
    public static DynamicParameters With(ITenantContext tenantContext, object? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);

        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("A tenant context is required for tenant-scoped queries.");

        var dynamicParameters = new DynamicParameters(parameters);
        dynamicParameters.Add("TenantId", tenantId);
        return dynamicParameters;
    }
}
