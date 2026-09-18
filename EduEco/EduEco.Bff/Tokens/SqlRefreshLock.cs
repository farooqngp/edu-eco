using System.Data;
using System.Data.Common;
using Dapper;
using EduEco.Infrastructure.Persistence.Dapper;

namespace EduEco.Bff.Tokens;

/// <summary>
/// Cluster-wide refresh lock on SQL Server (<c>sp_getapplock</c>, session-owned). Every BFF instance already shares the
/// session database, so no extra infrastructure is needed. The lock is released on dispose (or when the connection drops).
/// </summary>
public sealed class SqlRefreshLock(IDbConnectionFactory connectionFactory)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <returns>The held lock, or <c>null</c> when it could not be acquired within <paramref name="timeout"/>.</returns>
    public async Task<IAsyncDisposable?> AcquireAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connection = connectionFactory.CreateWriteConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var parameters = new DynamicParameters();
            parameters.Add("Resource", "bff:refresh:" + sessionId, DbType.String, size: 255);
            parameters.Add("LockMode", "Exclusive", DbType.String, size: 32);
            parameters.Add("LockOwner", "Session", DbType.String, size: 32);
            parameters.Add("LockTimeout", (int)timeout.TotalMilliseconds, DbType.Int32);
            parameters.Add("Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

            await connection.ExecuteAsync(new CommandDefinition(
                "sp_getapplock", parameters, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // 0 = granted, 1 = granted after waiting; negative = timeout / deadlock / error.
            if (parameters.Get<int>("Result") >= 0)
            {
                return new Handle(connection, "bff:refresh:" + sessionId);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    private sealed class Handle(DbConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "sp_releaseapplock", new { Resource = resource, LockOwner = "Session" }, commandType: CommandType.StoredProcedure))
                    .ConfigureAwait(false);
            }
            finally
            {
                // Closing the session releases the lock even if the explicit release failed.
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
