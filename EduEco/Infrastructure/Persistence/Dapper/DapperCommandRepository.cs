using Dapper;
using Dapper.Contrib.Extensions;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Abstractions.Security;
using EduEco.Core.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EduEco.Infrastructure.Persistence.Dapper;

/// <summary>
/// Generic write-side repository built on Dapper.Contrib.
/// Dapper.Contrib handles inserts and plain get/update; explicit SQL (<see cref="EntityMetadata{T}"/>) is used when
/// tenant predicates or optimistic concurrency are required, since Dapper.Contrib cannot express them.
/// </summary>
public sealed class DapperCommandRepository<T>(
    IDbSession session,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IOptions<DatabaseOptions> options) : ICommandRepository<T>
    where T : class, IEntity
{
    private readonly int _commandTimeout = options.Value.CommandTimeoutSeconds;

    public async Task<T?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var connection = await session.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!EntityMetadata<T>.IsTenantOwned)
        {
            return await connection.GetAsync<T>(id, session.Transaction, _commandTimeout).ConfigureAwait(false);
        }

        var tenantId = ResolveTenantFilter();
        var sql = tenantId is null ? EntityMetadata<T>.SelectById : EntityMetadata<T>.WithTenant(EntityMetadata<T>.SelectById);
        return await connection.QuerySingleOrDefaultAsync<T>(Command(sql, new { Id = id, TenantId = tenantId }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task InsertAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        PrepareInsert(entity);

        var connection = await session.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.InsertAsync(entity, session.Transaction, _commandTimeout).ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            throw new DuplicateEntityException($"{typeof(T).Name} violates a unique constraint.", ex);
        }

        await RefreshRowVersionAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task InsertRangeAsync(IReadOnlyCollection<T> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0)
        {
            return;
        }

        // Row-by-row so every entity receives its IDENTITY value (Dapper.Contrib's list insert does not populate keys).
        // For bulk loads use SqlBulkCopy / TVPs in a dedicated command instead.
        foreach (var entity in entities)
        {
            await InsertAsync(entity, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> UpdateAsync(T entity, CancellationToken cancellationToken = default)
    {
        try
        {
            return await UpdateCoreAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (IsDuplicateKey(ex))
        {
            throw new DuplicateEntityException($"{typeof(T).Name} violates a unique constraint.", ex);
        }
    }

    /// <summary>2601 = duplicate key in unique index, 2627 = unique/primary key constraint violation.</summary>
    private static bool IsDuplicateKey(SqlException exception) => exception.Number is 2601 or 2627;

    private async Task<bool> UpdateCoreAsync(T entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        long? tenantId = null;
        if (entity is ITenantOwned owned)
        {
            tenantId = ResolveTenantFilter();
            if (tenantId is not null && owned.TenantId != tenantId)
            {
                return false; // Never reveal or modify another tenant's row.
            }
        }

        if (entity is IAuditable auditable)
        {
            auditable.UpdatedAtUtc = timeProvider.GetUtcNow();
            auditable.UpdatedBy = currentUser.AuditName;
        }

        var connection = await session.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!EntityMetadata<T>.IsTenantOwned && !EntityMetadata<T>.IsConcurrencyAware)
        {
            return await connection.UpdateAsync(entity, session.Transaction, _commandTimeout).ConfigureAwait(false);
        }

        var sql = tenantId is null ? EntityMetadata<T>.UpdateById : EntityMetadata<T>.WithTenant(EntityMetadata<T>.UpdateById);

        if (entity is not IConcurrencyAware versioned)
        {
            var affected = await connection.ExecuteAsync(Command(sql, entity, cancellationToken)).ConfigureAwait(false);
            return affected > 0;
        }

        var newVersion = await connection.ExecuteScalarAsync<byte[]?>(Command(sql, entity, cancellationToken)).ConfigureAwait(false);
        if (newVersion is not null)
        {
            versioned.RowVersion = newVersion;
            return true;
        }

        var existsSql = tenantId is null ? EntityMetadata<T>.ExistsById : EntityMetadata<T>.WithTenant(EntityMetadata<T>.ExistsById);
        var exists = await connection.ExecuteScalarAsync<bool>(Command(existsSql, new { entity.Id, TenantId = tenantId }, cancellationToken))
            .ConfigureAwait(false);

        return exists
            ? throw new ConcurrencyException($"{typeof(T).Name} '{entity.Id}' was modified by another process.")
            : false;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var tenantId = EntityMetadata<T>.IsTenantOwned ? ResolveTenantFilter() : null;
        var sql = tenantId is null ? EntityMetadata<T>.DeleteById : EntityMetadata<T>.WithTenant(EntityMetadata<T>.DeleteById);

        var connection = await session.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Command(sql, new { Id = id, TenantId = tenantId }, cancellationToken))
            .ConfigureAwait(false);
        return affected > 0;
    }

    private void PrepareInsert(T entity)
    {
        if (EntityMetadata<T>.HasIdentityKey && entity.Id != 0)
        {
            throw new InvalidOperationException($"{typeof(T).Name}.Id is database-generated (IDENTITY); leave it as 0 on insert.");
        }

        if (!EntityMetadata<T>.HasIdentityKey && entity.Id <= 0)
        {
            throw new InvalidOperationException($"{typeof(T).Name}.Id is an explicit key and must be set before insert.");
        }

        if (entity is ITenantOwned owned)
        {
            var tenantId = ResolveTenantFilter();
            if (tenantId is not null)
            {
                if (owned.TenantId == 0)
                {
                    owned.TenantId = tenantId.Value;
                }
                else if (owned.TenantId != tenantId)
                {
                    throw new InvalidOperationException(
                        $"Cannot insert {typeof(T).Name} for tenant '{owned.TenantId}' from tenant context '{tenantId}'.");
                }
            }
            else if (owned.TenantId <= 0)
            {
                throw new InvalidOperationException($"{typeof(T).Name}.TenantId is required in a cross-tenant context.");
            }
        }

        if (entity is IAuditable auditable)
        {
            auditable.CreatedAtUtc = timeProvider.GetUtcNow();
            auditable.CreatedBy = currentUser.AuditName;
            auditable.UpdatedAtUtc = null;
            auditable.UpdatedBy = null;
        }
    }

    private async Task RefreshRowVersionAsync(T entity, CancellationToken cancellationToken)
    {
        if (entity is not IConcurrencyAware versioned)
        {
            return;
        }

        var connection = await session.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        versioned.RowVersion = await connection
            .ExecuteScalarAsync<byte[]>(Command(EntityMetadata<T>.SelectRowVersionById, new { entity.Id }, cancellationToken))
            .ConfigureAwait(false) ?? [];
    }

    /// <summary>Tenant id to filter on, or <c>null</c> in a trusted cross-tenant context.</summary>
    private long? ResolveTenantFilter() =>
        tenantContext.TenantId
        ?? (tenantContext.IsCrossTenant
            ? null
            : throw new InvalidOperationException($"A tenant context is required to access {typeof(T).Name}."));

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, session.Transaction, _commandTimeout, cancellationToken: cancellationToken);
}
