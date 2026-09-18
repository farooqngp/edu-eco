using Dapper.Contrib.Extensions;
using EduEco.Core.Common;

namespace EduEco.Core.Authorization;

/// <summary>Fine-grained right, e.g. <c>courses.write</c>. Reference data owned by DbUp scripts.</summary>
[Table("auth.Permissions")]
public sealed class Permission : IEntity
{
    [ExplicitKey]
    public long Id { get; set; }

    public required string Name { get; set; }

    public required string GroupName { get; set; }

    public required string Description { get; set; }
}
