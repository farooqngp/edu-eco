namespace EduEco.Application.Abstractions.Security;

/// <summary>Caller identity (user or client) for the current scope.</summary>
public interface ICurrentUser
{
    /// <summary>Numeric user id from <c>sub</c>; <c>null</c> for service clients and system jobs.</summary>
    long? UserId { get; }

    /// <summary><c>client_id</c> claim; <c>null</c> for system jobs.</summary>
    string? ClientId { get; }

    bool IsAuthenticated { get; }

    /// <summary><c>true</c> for client_credentials callers (no end user).</summary>
    bool IsServiceClient { get; }

    /// <summary>Stable identifier written to audit columns (e.g. <c>user:42</c>, <c>client:sync-svc</c>).</summary>
    string AuditName { get; }
}
