using System.Globalization;
using System.Security.Claims;
using EduEco.Core.Authorization;

namespace EduEco.Api.Security;

/// <summary>Reads RFC 9068 access token claims (inbound claim mapping is disabled, so JWT names are used verbatim).</summary>
public static class ClaimsPrincipalExtensions
{
    public const string SubjectClaim = "sub";
    public const string ClientIdClaim = "client_id";
    public const string ScopeClaim = "scope";
    public const string RoleClaim = "role";

    public static string? GetSubject(this ClaimsPrincipal principal) => principal.FindFirst(SubjectClaim)?.Value;

    public static string? GetClientId(this ClaimsPrincipal principal) => principal.FindFirst(ClientIdClaim)?.Value;

    /// <summary>client_credentials tokens use the client id as subject (no end user).</summary>
    public static bool IsServiceClient(this ClaimsPrincipal principal) =>
        principal.GetSubject() is { } subject && string.Equals(subject, principal.GetClientId(), StringComparison.Ordinal);

    public static long? GetUserId(this ClaimsPrincipal principal) =>
        !principal.IsServiceClient() && long.TryParse(principal.GetSubject(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    public static long? GetTenantId(this ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst(EduEcoClaimTypes.TenantId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    public static bool HasTenantClaim(this ClaimsPrincipal principal) => principal.HasClaim(c => c.Type == EduEcoClaimTypes.TenantId);

    public static IReadOnlySet<string> GetScopes(this ClaimsPrincipal principal) =>
        principal.FindAll(ScopeClaim)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

    public static bool HasScope(this ClaimsPrincipal principal, string scope) => principal.GetScopes().Contains(scope);
}
