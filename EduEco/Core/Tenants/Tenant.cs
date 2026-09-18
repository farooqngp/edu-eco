using Dapper.Contrib.Extensions;
using EduEco.Core.Common;

namespace EduEco.Core.Tenants;

/// <summary>School or district.</summary>
[Table("dbo.Tenants")]
public sealed class Tenant : IEntity, IAuditable, IConcurrencyAware
{
    [Key]
    public long Id { get; set; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    [Computed]
    public byte[] RowVersion { get; set; } = [];
}
