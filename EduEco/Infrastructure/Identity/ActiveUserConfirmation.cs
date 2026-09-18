using EduEco.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace EduEco.Infrastructure.Identity;

/// <summary>
/// Sign-in gate used by <c>SignInManager.CanSignInAsync</c> (password, passkey, 2FA and token refresh):
/// deactivated or unconfirmed accounts cannot authenticate.
/// </summary>
public sealed class ActiveUserConfirmation : IUserConfirmation<ApplicationUser>
{
    public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.IsActive && user.EmailConfirmed);
    }
}
