using Dapper.Contrib.Extensions;
using EduEco.Core.Common;

namespace EduEco.Core.Authorization;

/// <summary>Single-use, expiring code that lets a new user join a tenant with a given role at self-registration.</summary>
[Table("auth.TenantInvites")]
public sealed class TenantInvite : IEntity, ITenantOwned, IAuditable, IConcurrencyAware
{
    [Key]
    public long Id { get; set; }

    public long TenantId { get; set; }

    /// <summary>SHA-256 of the plaintext code (Base64Url). The plaintext is never persisted.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public long RoleId { get; set; }

    /// <summary>Address the code was emailed to at issuance. Null for invites issued before this was captured.</summary>
    public string? InviteeEmail { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public int MaxUses { get; set; } = 1;

    public int UseCount { get; set; }

    public long? RedeemedByUserId { get; set; }

    public DateTimeOffset? RedeemedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    [Computed]
    public byte[] RowVersion { get; set; } = [];
}
