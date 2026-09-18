using P = EduEco.Core.Authorization.Permissions;

namespace EduEco.Core.Authorization;

public sealed record RoleDefinition(long Id, string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>
/// System roles and role-permission matrix. IDs 1-999 are reserved for system roles (IDENTITY reseeded to 1000). Mirrored in
/// <c>EduEco.Database/Scripts/02_ReferenceData/R0001__roles.sql</c> and <c>R0003__role_permissions.sql</c> (verified by tests).
/// </summary>
public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string TenantAdmin = "TenantAdmin";
    public const string Teacher = "Teacher";
    public const string Student = "Student";
    public const string Parent = "Parent";

    public static IReadOnlyList<RoleDefinition> All { get; } =
    [
        new(1, PlatformAdmin, "Platform-wide administrator",
            [.. P.All.Select(p => p.Name)]),
        new(2, TenantAdmin, "School or district administrator",
            [P.Tenants.Read, P.Users.Manage, P.Courses.Read, P.Courses.Write, P.Grades.Read, P.Grades.Write]),
        new(3, Teacher, "Teacher",
            [P.Tenants.Read, P.Courses.Read, P.Courses.Write, P.Grades.Read, P.Grades.Write]),
        new(4, Student, "Student",
            [P.Tenants.Read, P.Courses.Read, P.Grades.Read]),
        new(5, Parent, "Parent or guardian",
            [P.Tenants.Read, P.Courses.Read, P.Grades.Read]),
    ];
}
