using S = EduEco.Core.Authorization.Scopes;

namespace EduEco.Core.Authorization;

/// <param name="Id">Fixed id (code-owned, no IDENTITY).</param>
/// <param name="Name">Permission name used by <c>[HasPermission]</c>.</param>
/// <param name="GroupName">UI grouping.</param>
/// <param name="Description">Human readable description.</param>
/// <param name="RequiredScope">OAuth scope the calling client must hold (delegation ceiling), in addition to the permission.</param>
/// <param name="AllowServiceClients">Whether client_credentials tokens may exercise it (scope-only, no user).</param>
/// <param name="TenantScoped">Whether the token must carry a <c>tenant_id</c>.</param>
public sealed record PermissionDefinition(
    long Id,
    string Name,
    string GroupName,
    string Description,
    string RequiredScope,
    bool AllowServiceClients,
    bool TenantScoped);

/// <summary>
/// Permission catalogue. IDs are fixed (code-owned, no IDENTITY) and mirrored in
/// <c>EduEco.Database/Scripts/02_ReferenceData/R0002__permissions.sql</c> (verified by tests).
/// </summary>
public static class Permissions
{
    public static class Tenants
    {
        public const string Read = "tenants.read";
        public const string Manage = "tenants.manage";
    }

    public static class Users
    {
        public const string Manage = "users.manage";
    }

    public static class Courses
    {
        public const string Read = "courses.read";
        public const string Write = "courses.write";
    }

    public static class Grades
    {
        public const string Read = "grades.read";
        public const string Write = "grades.write";
    }

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(1, Tenants.Manage, "Tenants", "Create and manage tenants", S.ApiWrite, AllowServiceClients: false, TenantScoped: false),
        new(2, Users.Manage, "Users", "Manage users and memberships within a tenant", S.ApiWrite, AllowServiceClients: false, TenantScoped: true),
        new(3, Courses.Read, "Courses", "View courses", S.ApiRead, AllowServiceClients: true, TenantScoped: true),
        new(4, Courses.Write, "Courses", "Create and edit courses", S.ApiWrite, AllowServiceClients: true, TenantScoped: true),
        new(5, Grades.Read, "Grades", "View grades", S.ApiRead, AllowServiceClients: true, TenantScoped: true),
        new(6, Grades.Write, "Grades", "Record and edit grades", S.ApiWrite, AllowServiceClients: true, TenantScoped: true),
        new(7, Tenants.Read, "Tenants", "View the current tenant profile", S.ApiRead, AllowServiceClients: true, TenantScoped: true),
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByName = All.ToDictionary(p => p.Name, StringComparer.Ordinal);

    public static PermissionDefinition? Find(string name) => ByName.GetValueOrDefault(name);
}
