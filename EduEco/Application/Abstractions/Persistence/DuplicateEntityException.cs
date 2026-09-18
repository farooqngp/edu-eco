namespace EduEco.Application.Abstractions.Persistence;

/// <summary>A unique constraint was violated (maps to HTTP 409).</summary>
public sealed class DuplicateEntityException : Exception
{
    public DuplicateEntityException()
    {
    }

    public DuplicateEntityException(string message)
        : base(message)
    {
    }

    public DuplicateEntityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
