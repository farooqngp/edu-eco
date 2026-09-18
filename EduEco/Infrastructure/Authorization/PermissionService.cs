using System.Globalization;
using EduEco.Application.Abstractions.Security;
using EduEco.Application.Authorization;
using EduEco.Application.Tenants;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace EduEco.Infrastructure.Authorization;

/// <summary>
/// Server-side permission resolution (permissions are never embedded in tokens) cached per instance with
/// <see cref="HybridCache"/>. Entries are tagged per user and tenant; invalidations are applied locally and broadcast to
/// every instance through <see cref="ICacheInvalidationBus"/> (Redis pub/sub when scaled out). Entries deliberately skip the
/// distributed L2: a shared copy could not be evicted reliably by tag and a DB lookup is cheap.
/// </summary>
public sealed class PermissionService(
    IPermissionQueries permissionQueries,
    ITenantQueries tenantQueries,
    HybridCache cache,
    ICacheInvalidationBus invalidationBus,
    IOptions<AuthorizationCacheOptions> options) : IPermissionService, IPermissionCacheInvalidator, ITenantStatusService
{
    private readonly AuthorizationCacheOptions _options = options.Value;

    public async Task<bool> HasPermissionAsync(long userId, long tenantId, string permission, CancellationToken cancellationToken = default) =>
        (await GetPermissionsAsync(userId, tenantId, cancellationToken).ConfigureAwait(false)).Contains(permission);

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(long userId, long tenantId, CancellationToken cancellationToken = default)
    {
        string[] names;
        if (_options.PermissionCacheDuration <= TimeSpan.Zero)
        {
            names = [.. await permissionQueries.GetPermissionNamesAsync(userId, tenantId, cancellationToken).ConfigureAwait(false)];
        }
        else
        {
            names = await cache.GetOrCreateAsync(
                string.Create(CultureInfo.InvariantCulture, $"perm:{userId}:{tenantId}"),
                (permissionQueries, userId, tenantId),
                static async (state, ct) => (await state.permissionQueries.GetPermissionNamesAsync(state.userId, state.tenantId, ct).ConfigureAwait(false)).ToArray(),
                Entry(_options.PermissionCacheDuration),
                [UserTag(userId), TenantTag(tenantId)],
                cancellationToken).ConfigureAwait(false);
        }

        return names.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> IsActiveAsync(long tenantId, CancellationToken cancellationToken = default)
    {
        if (_options.TenantStatusCacheDuration <= TimeSpan.Zero)
        {
            return await LoadTenantActiveAsync(tenantQueries, tenantId, cancellationToken).ConfigureAwait(false);
        }

        return await cache.GetOrCreateAsync(
            string.Create(CultureInfo.InvariantCulture, $"tenant-active:{tenantId}"),
            (tenantQueries, tenantId),
            static (state, ct) => new ValueTask<bool>(LoadTenantActiveAsync(state.tenantQueries, state.tenantId, ct)),
            Entry(_options.TenantStatusCacheDuration),
            [TenantTag(tenantId)],
            cancellationToken).ConfigureAwait(false);
    }

    public Task InvalidateUserAsync(long userId, CancellationToken cancellationToken = default) =>
        InvalidateAsync(UserTag(userId), cancellationToken);

    public Task InvalidateTenantAsync(long tenantId, CancellationToken cancellationToken = default) =>
        InvalidateAsync(TenantTag(tenantId), cancellationToken);

    private async Task InvalidateAsync(string tag, CancellationToken cancellationToken)
    {
        await cache.RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);
        await invalidationBus.PublishAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> LoadTenantActiveAsync(ITenantQueries queries, long tenantId, CancellationToken cancellationToken) =>
        await queries.FindActiveTenantByIdAsync(tenantId, cancellationToken).ConfigureAwait(false) is not null;

    private static HybridCacheEntryOptions Entry(TimeSpan duration) => new()
    {
        Expiration = duration,
        LocalCacheExpiration = duration,
        Flags = HybridCacheEntryFlags.DisableDistributedCache,
    };

    private static string UserTag(long userId) => string.Create(CultureInfo.InvariantCulture, $"user:{userId}");

    private static string TenantTag(long tenantId) => string.Create(CultureInfo.InvariantCulture, $"tenant:{tenantId}");
}
