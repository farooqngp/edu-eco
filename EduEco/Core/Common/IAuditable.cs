namespace EduEco.Core.Common;

/// <summary>Audit columns stamped by the command repository.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }

    string CreatedBy { get; set; }

    DateTimeOffset? UpdatedAtUtc { get; set; }

    string? UpdatedBy { get; set; }
}
