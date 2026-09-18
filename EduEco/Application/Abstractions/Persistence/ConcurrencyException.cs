namespace EduEco.Application.Abstractions.Persistence;

/// <summary>Optimistic concurrency conflict: the row was modified by another writer.</summary>
public sealed class ConcurrencyException : Exception
{
    public ConcurrencyException()
    {
    }

    public ConcurrencyException(string message)
        : base(message)
    {
    }

    public ConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
