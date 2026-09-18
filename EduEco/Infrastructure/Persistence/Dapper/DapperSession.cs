using System.Data;
using System.Data.Common;
using EduEco.Application.Abstractions.Persistence;

namespace EduEco.Infrastructure.Persistence.Dapper;

/// <summary>Connection + optional transaction shared by command repositories within a DI scope.</summary>
public interface IDbSession
{
    DbTransaction? Transaction { get; }

    Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Scoped unit of work: lazily opens one write connection per scope.</summary>
public sealed class DapperSession(IDbConnectionFactory connectionFactory) : IUnitOfWork, IDbSession
{
    private DbConnection? _connection;
    private bool _disposed;

    public DbTransaction? Transaction { get; private set; }

    public bool HasActiveTransaction => Transaction is not null;

    public async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _connection ??= connectionFactory.CreateWriteConnection();
        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return _connection;
    }

    public async Task BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
    {
        if (Transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active for this unit of work.");
        }

        var connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        Transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var transaction = Transaction ?? throw new InvalidOperationException("No active transaction to commit.");
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ClearTransactionAsync().ConfigureAwait(false);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var transaction = Transaction ?? throw new InvalidOperationException("No active transaction to roll back.");
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ClearTransactionAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Uncommitted work is rolled back implicitly by disposing the transaction.
        await ClearTransactionAsync().ConfigureAwait(false);

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    private async ValueTask ClearTransactionAsync()
    {
        if (Transaction is not null)
        {
            await Transaction.DisposeAsync().ConfigureAwait(false);
            Transaction = null;
        }
    }
}
