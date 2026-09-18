namespace EduEco.Core.Authorization;

/// <summary>Custom OpenIddict application (client) property keys.</summary>
public static class ClientProperties
{
    /// <summary>Tenant a client_credentials client acts for (JSON number). Absent for platform-level services.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>OpenID Connect Back-Channel Logout 1.0 endpoint of the client (JSON string).</summary>
    public const string BackchannelLogoutUri = "backchannel_logout_uri";

    /// <summary>RFC 9449 client metadata: access tokens must be DPoP-bound (JSON boolean).</summary>
    public const string DPoPBoundAccessTokens = "dpop_bound_access_tokens";
}
