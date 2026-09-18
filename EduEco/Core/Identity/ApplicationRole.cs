using Microsoft.AspNetCore.Identity;

namespace EduEco.Core.Identity;

/// <summary>ASP.NET Core Identity role (EF Core auth store; schema owned by DbUp).</summary>
public sealed class ApplicationRole : IdentityRole<long>
{
    public string? Description { get; set; }

    public bool IsSystem { get; set; }
}
