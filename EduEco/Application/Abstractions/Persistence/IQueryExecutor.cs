namespace EduEco.Application.Abstractions.Persistence;

/// <summary>
/// Read-side SQL executor (read connection, no tracking, no transaction).
/// Tenant-owned queries must filter on <c>@TenantId</c>.
/// </summary>
public interface IQueryExecutor
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> ExecuteScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Runs a multi-result-set batch; the reader is only valid inside <paramref name="map"/>.</summary>
    Task<TResult> QueryMultipleAsync<TResult>(
        string sql,
        object? parameters,
        Func<IMultipleResultReader, Task<TResult>> map,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Paged query in one round trip. <paramref name="sql"/> must contain an <c>ORDER BY</c>;
    /// <c>OFFSET/FETCH</c> is appended. <paramref name="countSql"/> must return a single integer.
    /// </summary>
    Task<PagedResult<T>> QueryPagedAsync<T>(
        string sql,
        string countSql,
        object? parameters,
        PageRequest page,
        CancellationToken cancellationToken = default);
}

public interface IMultipleResultReader
{
    Task<IReadOnlyList<T>> ReadAsync<T>();

    Task<T?> ReadSingleOrDefaultAsync<T>();

    Task<T?> ReadFirstOrDefaultAsync<T>();
}
