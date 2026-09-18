using System.ComponentModel.DataAnnotations;

namespace EduEco.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string ConnectionStringName = "EduEco";
    public const string ReadConnectionStringName = "EduEcoRead";

    /// <summary>Primary (read/write) connection string.</summary>
    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Optional read replica connection string; falls back to <see cref="ConnectionString"/>.</summary>
    public string? ReadConnectionString { get; set; }

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}
