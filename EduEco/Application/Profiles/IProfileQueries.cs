using EduEco.Core.Identity;

namespace EduEco.Application.Profiles;

public interface IProfileQueries
{
    /// <summary>Not tenant-scoped: a profile is a person attribute, shared across every tenant the user belongs to.</summary>
    Task<UserProfile?> FindByUserIdAsync(long userId, CancellationToken cancellationToken = default);
}
