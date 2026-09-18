namespace EduEco.Core.Common;

/// <summary>
/// Optimistic concurrency via SQL Server <c>rowversion</c>.
/// Implementations must mark <see cref="RowVersion"/> with <c>[Computed]</c>.
/// </summary>
public interface IConcurrencyAware
{
    byte[] RowVersion { get; set; }
}
