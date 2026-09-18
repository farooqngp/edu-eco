namespace EduEco.Core.Common;

/// <summary>Row belongs to exactly one tenant; repositories enforce tenant isolation.</summary>
public interface ITenantOwned
{
    long TenantId { get; set; }
}
