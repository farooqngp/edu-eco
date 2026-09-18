namespace EduEco.Identity.Auditing;

/// <summary>
/// Security audit events (structured, stable event ids 5000-5099) for SIEM ingestion.
/// Never log secrets, tokens, passwords or email addresses; identify principals by id.
/// </summary>
internal static partial class AuditLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "AUDIT sign-in succeeded: user {UserId} via {Method}")]
    public static partial void SignInSucceeded(ILogger logger, long userId, string method);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "AUDIT sign-in succeeded via passkey")]
    public static partial void PasskeySignInSucceeded(ILogger logger);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "AUDIT sign-in failed via {Method}: {Reason}")]
    public static partial void SignInFailed(ILogger logger, string method, string reason);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "AUDIT account locked out: user {UserId}")]
    public static partial void LockedOut(ILogger logger, long userId);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "AUDIT signed out: user {UserId}")]
    public static partial void SignedOut(ILogger logger, long? userId);

    [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "AUDIT two-factor enabled: user {UserId}")]
    public static partial void TwoFactorEnabled(ILogger logger, long userId);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning, Message = "AUDIT two-factor disabled: user {UserId}")]
    public static partial void TwoFactorDisabled(ILogger logger, long userId);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Information, Message = "AUDIT passkey registered: user {UserId}")]
    public static partial void PasskeyRegistered(ILogger logger, long userId);

    [LoggerMessage(EventId = 5013, Level = LogLevel.Warning, Message = "AUDIT passkey removed: user {UserId}")]
    public static partial void PasskeyRemoved(ILogger logger, long userId);

    [LoggerMessage(EventId = 5020, Level = LogLevel.Information, Message = "AUDIT authorization granted: client {ClientId}, user {UserId}, tenant {TenantId}")]
    public static partial void AuthorizationGranted(ILogger logger, string? clientId, long userId, long tenantId);

    [LoggerMessage(EventId = 5021, Level = LogLevel.Warning, Message = "AUDIT authorization denied: client {ClientId}, reason {Reason}")]
    public static partial void AuthorizationDenied(ILogger logger, string? clientId, string reason);

    [LoggerMessage(EventId = 5030, Level = LogLevel.Information, Message = "AUDIT token issued: client {ClientId}, grant {GrantType}, subject {Subject}, tenant {TenantId}")]
    public static partial void TokenIssued(ILogger logger, string? clientId, string? grantType, string? subject, long? tenantId);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Information, Message = "AUDIT token exchanged: client {ClientId} acting for subject {Subject}, audience {Audience}")]
    public static partial void TokenExchanged(ILogger logger, string? clientId, string? subject, string audience);

    [LoggerMessage(EventId = 5040, Level = LogLevel.Information, Message = "AUDIT global logout: subject {Subject}, {ClientCount} client(s) notified")]
    public static partial void GlobalLogout(ILogger logger, string subject, int clientCount);

    [LoggerMessage(EventId = 5041, Level = LogLevel.Information, Message = "AUDIT password reset requested")]
    public static partial void PasswordResetRequested(ILogger logger);

    [LoggerMessage(EventId = 5042, Level = LogLevel.Warning, Message = "AUDIT password reset completed: user {UserId}")]
    public static partial void PasswordResetCompleted(ILogger logger, long userId);

    [LoggerMessage(EventId = 5043, Level = LogLevel.Information, Message = "AUDIT email confirmed: user {UserId}")]
    public static partial void EmailConfirmed(ILogger logger, long userId);

    [LoggerMessage(EventId = 5044, Level = LogLevel.Information, Message = "AUDIT re-authentication succeeded: user {UserId}")]
    public static partial void Reauthenticated(ILogger logger, long userId);

    [LoggerMessage(EventId = 5045, Level = LogLevel.Information, Message = "AUDIT user registered: user {UserId}")]
    public static partial void UserRegistered(ILogger logger, long userId);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Warning, Message = "AUDIT token request rejected: client {ClientId}, grant {GrantType}, reason {Reason}")]
    public static partial void TokenRejected(ILogger logger, string? clientId, string? grantType, string reason);
}
