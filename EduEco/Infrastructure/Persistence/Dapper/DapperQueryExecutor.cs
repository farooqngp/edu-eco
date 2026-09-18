using System.Data.Common;
using Dapper;
using EduEco.Application.Abstractions.Persistence;
using Microsoft.Extensions.Options;

namespace EduEco.Infrastructure.Persistence.Dapper;

/// <summary>Read-side executor: one short-lived read connection per call.</summary>
public sealed class DapperQueryExecutor(IDbConnectionFactory connectionFactory, IOptions<DatabaseOptions> options) : IQueryExecutor
{
    private readonly int _commandTimeout = options.Value.CommandTimeoutSeconds;

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<T>(Command(sql, parameters, cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<T>(Command(sql, parameters, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<T>(Command(sql, parameters, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<T>(Command(sql, parameters, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<TResult> QueryMultipleAsync<TResult>(
        string sql,
        object? parameters,
        Func<IMultipleResultReader, Task<TResult>> map,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var grid = await connection.QueryMultipleAsync(Command(sql, parameters, cancellationToken)).ConfigureAwait(false);
        return await map(new GridReaderAdapter(grid)).ConfigureAwait(false);
    }

    public async Task<PagedResult<T>> QueryPagedAsync<T>(
        string sql,
        string countSql,
        object? parameters,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(countSql);
        ArgumentNullException.ThrowIfNull(page);

        if (!sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Paged SQL must contain ORDER BY for deterministic OFFSET/FETCH.", nameof(sql));
        }

        var batch = $"""
            {countSql.TrimEnd().TrimEnd(';')};
            {sql.TrimEnd().TrimEnd(';')}
            OFFSET @PageOffset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var dynamicParameters = new DynamicParameters(parameters);
        dynamicParameters.Add("PageOffset", page.Offset);
        dynamicParameters.Add("PageSize", page.PageSize);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var grid = await connection.QueryMultipleAsync(Command(batch, dynamicParameters, cancellationToken)).ConfigureAwait(false);

        var total = await grid.ReadSingleAsync<int>().ConfigureAwait(false);
        var items = (await grid.ReadAsync<T>().ConfigureAwait(false)).AsList();

        return new PagedResult<T>(items, page.PageNumber, page.PageSize, total);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.CreateReadConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private CommandDefinition Command(string sql, object? parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, commandTimeout: _commandTimeout, cancellationToken: cancellationToken);

    private sealed class GridReaderAdapter(SqlMapper.GridReader grid) : IMultipleResultReader
    {
        public async Task<IReadOnlyList<T>> ReadAsync<T>() => (await grid.ReadAsync<T>().ConfigureAwait(false)).AsList();

        public Task<T?> ReadSingleOrDefaultAsync<T>() => grid.ReadSingleOrDefaultAsync<T?>();

        public Task<T?> ReadFirstOrDefaultAsync<T>() => grid.ReadFirstOrDefaultAsync<T?>();
    }
}
