using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Invites;

namespace EduEco.Infrastructure.Queries;

public sealed class InviteQueries(IQueryExecutor queryExecutor) : IInviteQueries
{
    public Task<InviteLookup?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken = default) =>
        queryExecutor.QuerySingleOrDefaultAsync<InviteLookup>(
            """
            SELECT i.Id, i.TenantId, r.Name AS RoleName, i.ExpiresAtUtc, i.MaxUses, i.UseCount
            FROM auth.TenantInvites i
            INNER JOIN auth.AspNetRoles r ON r.Id = i.RoleId
            WHERE i.CodeHash = @CodeHash
            """,
            new { CodeHash = codeHash },
            cancellationToken);
}
