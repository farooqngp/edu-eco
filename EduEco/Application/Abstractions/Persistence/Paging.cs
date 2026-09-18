namespace EduEco.Application.Abstractions.Persistence;

public sealed record PageRequest
{
    public const int MaxPageSize = 200;

    public PageRequest(int pageNumber = 1, int pageSize = 25)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    public int PageNumber { get; }

    public int PageSize { get; }

    public int Offset => (PageNumber - 1) * PageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
