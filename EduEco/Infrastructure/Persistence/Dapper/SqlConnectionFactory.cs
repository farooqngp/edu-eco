using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EduEco.Infrastructure.Persistence.Dapper;

public interface IDbConnectionFactory
{
    /// <summary>Creates a new (closed) connection to the primary database.</summary>
    DbConnection CreateWriteConnection();

    /// <summary>Creates a new (closed) connection to the read replica, or the primary when none is configured.</summary>
    DbConnection CreateReadConnection();
}

public sealed class SqlConnectionFactory(IOptions<DatabaseOptions> options) : IDbConnectionFactory
{
    private readonly DatabaseOptions _options = options.Value;

    public DbConnection CreateWriteConnection() => new SqlConnection(_options.ConnectionString);

    public DbConnection CreateReadConnection() =>
        new SqlConnection(string.IsNullOrWhiteSpace(_options.ReadConnectionString)
            ? _options.ConnectionString
            : _options.ReadConnectionString);
}
