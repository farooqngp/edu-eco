namespace EduEco.Core.Common;

/// <summary>Entity persisted through the generic command repository.</summary>
public interface IEntity
{
    long Id { get; set; }
}
