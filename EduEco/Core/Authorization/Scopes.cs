namespace EduEco.Core.Authorization;

/// <summary>OAuth scopes (client delegation) exposed by the EduEco API resource.</summary>
public static class Scopes
{
    public const string ApiRead = "api.read";
    public const string ApiWrite = "api.write";
    public const string ApiSync = "api.sync";

    /// <summary>Downstream reporting service; reachable only through token exchange (RFC 8693).</summary>
    public const string ReportingRead = "reporting.read";
}

/// <summary>OAuth resource (token audience) identifiers.</summary>
public static class Resources
{
    public const string Api = "eduEco-api";

    /// <summary>Downstream service receiving delegated (exchanged) tokens.</summary>
    public const string Reporting = "eduEco-reporting";
}

/// <summary>Custom JWT claim types.</summary>
public static class EduEcoClaimTypes
{
    public const string TenantId = "tenant_id";
}
