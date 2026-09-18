namespace EduEco.Core.Authorization;

/// <summary>Role to permission link (composite key; maintained by reference-data scripts).</summary>
public sealed class RolePermission
{
    public long RoleId { get; set; }

    public long PermissionId { get; set; }
}
