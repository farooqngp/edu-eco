using Dapper.Contrib.Extensions;
using EduEco.Core.Common;

namespace EduEco.Core.Authorization;

/// <summary>Tenant-scoped role assignment for a user.</summary>
[Table("auth.UserTenantMemberships")]
public sealed class UserTenantMembership : IEntity, ITenantOwned, IAuditable
{
    [Key]
    public long Id { get; set; }

    public long TenantId { get; set; }

    public long UserId { get; set; }

    public long RoleId { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}
