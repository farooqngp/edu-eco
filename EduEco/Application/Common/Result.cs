namespace EduEco.Application.Common;

public enum ResultError
{
    None,
    NotFound,
    Validation,
    Conflict,
    Forbidden,
}

/// <summary>Outcome of an application use case; mapped to RFC 9457 problem details by the API.</summary>
public sealed record Result<T>(T? Value, ResultError Error, string? Detail)
{
    public bool Succeeded => Error == ResultError.None;

    public static Result<T> Ok(T value) => new(value, ResultError.None, null);

    public static Result<T> Fail(ResultError error, string detail) => new(default, error, detail);
}
