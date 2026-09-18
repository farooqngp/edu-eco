using System.Reflection;
using Dapper.Contrib.Extensions;
using EduEco.Core.Common;

namespace EduEco.Infrastructure.Persistence.Dapper;

/// <summary>
/// Per-entity SQL built once from Dapper.Contrib attributes. Used where Dapper.Contrib lacks
/// tenant predicates, optimistic concurrency or cancellation support.
/// </summary>
internal static class EntityMetadata<T>
    where T : class, IEntity
{
    private static readonly HashSet<string> InsertOnlyColumns =
        [nameof(IEntity.Id), nameof(ITenantOwned.TenantId), nameof(IAuditable.CreatedAtUtc), nameof(IAuditable.CreatedBy)];

    static EntityMetadata()
    {
        var type = typeof(T);
        TableName = type.GetCustomAttribute<TableAttribute>()?.Name
            ?? throw new InvalidOperationException($"{type.Name} must declare [Table(\"schema.Table\")] to use the generic repository.");

        var idProperty = type.GetProperty(nameof(IEntity.Id))!;
        HasIdentityKey = idProperty.GetCustomAttribute<KeyAttribute>() is not null;
        if (!HasIdentityKey && idProperty.GetCustomAttribute<ExplicitKeyAttribute>() is null)
        {
            throw new InvalidOperationException(
                $"{type.Name}.Id must be marked [Key] (bigint IDENTITY) or [ExplicitKey] (code-owned reference data).");
        }

        IsTenantOwned = typeof(ITenantOwned).IsAssignableFrom(type);
        IsAuditable = typeof(IAuditable).IsAssignableFrom(type);
        IsConcurrencyAware = typeof(IConcurrencyAware).IsAssignableFrom(type);

        var updateColumns = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true })
            .Where(p => p.GetCustomAttribute<ComputedAttribute>() is null)
            .Where(p => p.GetCustomAttribute<WriteAttribute>() is not { Write: false })
            .Select(p => p.Name)
            .Where(name => !InsertOnlyColumns.Contains(name))
            .ToArray();

        if (updateColumns.Length == 0)
        {
            throw new InvalidOperationException($"{type.Name} has no updatable columns.");
        }

        var setClause = string.Join(", ", updateColumns.Select(c => $"[{c}] = @{c}"));
        var output = IsConcurrencyAware ? $" OUTPUT INSERTED.[{nameof(IConcurrencyAware.RowVersion)}]" : string.Empty;
        var concurrency = IsConcurrencyAware ? $" AND [{nameof(IConcurrencyAware.RowVersion)}] = @{nameof(IConcurrencyAware.RowVersion)}" : string.Empty;

        SelectById = $"SELECT * FROM {TableName} WHERE [Id] = @Id";
        UpdateById = $"UPDATE {TableName} SET {setClause}{output} WHERE [Id] = @Id{concurrency}";
        DeleteById = $"DELETE FROM {TableName} WHERE [Id] = @Id";
        ExistsById = $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {TableName} WHERE [Id] = @Id) THEN 1 ELSE 0 END";
        SelectRowVersionById = $"SELECT [{nameof(IConcurrencyAware.RowVersion)}] FROM {TableName} WHERE [Id] = @Id";
    }

    public static string TableName { get; }

    /// <summary><c>true</c> for <c>[Key]</c> (bigint IDENTITY, populated on insert); <c>false</c> for <c>[ExplicitKey]</c>.</summary>
    public static bool HasIdentityKey { get; }

    public static bool IsTenantOwned { get; }

    public static bool IsAuditable { get; }

    public static bool IsConcurrencyAware { get; }

    public static string SelectById { get; }

    public static string UpdateById { get; }

    public static string DeleteById { get; }

    public static string ExistsById { get; }

    public static string SelectRowVersionById { get; }

    public static string WithTenant(string sql) => $"{sql} AND [TenantId] = @TenantId";
}
