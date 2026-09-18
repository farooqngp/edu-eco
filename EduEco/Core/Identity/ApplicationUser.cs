using Microsoft.AspNetCore.Identity;

namespace EduEco.Core.Identity;

/// <summary>ASP.NET Core Identity user (EF Core auth store; schema owned by DbUp).</summary>
public sealed class ApplicationUser : IdentityUser<long>
{
    public string? DisplayName { get; set; }

    public bool IsActive { get; set; } = true;
}
