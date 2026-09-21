using Dapper.Contrib.Extensions;
using EduEco.Core.Common;

namespace EduEco.Core.Identity;

/// <summary>Personal profile data beyond <see cref="ApplicationUser.DisplayName"/>. One row per user, not tenant-scoped.</summary>
[Table("dbo.UserProfiles")]
public sealed class UserProfile : IEntity, IAuditable, IConcurrencyAware
{
    [Key]
    public long Id { get; set; }

    public long UserId { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? PostalCode { get; set; }

    public string? Country { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    [Computed]
    public byte[] RowVersion { get; set; } = [];
}
