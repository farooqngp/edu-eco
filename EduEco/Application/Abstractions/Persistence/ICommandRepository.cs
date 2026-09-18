using EduEco.Core.Common;

namespace EduEco.Application.Abstractions.Persistence;

/// <summary>
/// Generic write-side repository. Operations enlist in the ambient <see cref="IUnitOfWork"/> transaction when one is active.
/// Tenant isolation (<see cref="ITenantOwned"/>), auditing (<see cref="IAuditable"/>) and optimistic concurrency
/// (<see cref="IConcurrencyAware"/>) are enforced by the implementation.
/// </summary>
public interface ICommandRepository<T>
    where T : class, IEntity
{
    /// <summary>Returns the entity or <c>null</c> when not found or owned by another tenant.</summary>
    Task<T?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    Task InsertAsync(T entity, CancellationToken cancellationToken = default);

    Task InsertRangeAsync(IReadOnlyCollection<T> entities, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>false</c> when no row matched (not found / other tenant).</summary>
    /// <exception cref="ConcurrencyException">Row version changed since the entity was read.</exception>
    Task<bool> UpdateAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Returns <c>false</c> when no row matched (not found / other tenant).</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);
}
