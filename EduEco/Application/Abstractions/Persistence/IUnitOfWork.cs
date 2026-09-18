using System.Data;

namespace EduEco.Application.Abstractions.Persistence;

/// <summary>Scoped transaction boundary shared by all command repositories in the same scope.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    bool HasActiveTransaction { get; }

    Task BeginAsync(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
