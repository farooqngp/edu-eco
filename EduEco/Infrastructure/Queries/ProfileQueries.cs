using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Profiles;
using EduEco.Core.Identity;

namespace EduEco.Infrastructure.Queries;

public sealed class ProfileQueries(IQueryExecutor queryExecutor) : IProfileQueries
{
    public Task<UserProfile?> FindByUserIdAsync(long userId, CancellationToken cancellationToken = default) =>
        queryExecutor.QuerySingleOrDefaultAsync<UserProfile>(
            "SELECT * FROM dbo.UserProfiles WHERE UserId = @UserId",
            new { UserId = userId },
            cancellationToken);
}
