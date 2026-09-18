using EduEco.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace EduEco.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Maps an application failure to an RFC 9457 response.</summary>
    protected ObjectResult ProblemFor<T>(Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var status = result.Error switch
        {
            ResultError.NotFound => StatusCodes.Status404NotFound,
            ResultError.Validation => StatusCodes.Status400BadRequest,
            ResultError.Conflict => StatusCodes.Status409Conflict,
            ResultError.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Problem(detail: result.Detail, statusCode: status);
    }
}

/// <summary>Standard paging query parameters (bounded to protect the database).</summary>
public sealed class PagingQuery
{
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [System.ComponentModel.DataAnnotations.Range(1, EduEco.Application.Abstractions.Persistence.PageRequest.MaxPageSize)]
    public int PageSize { get; init; } = 25;

    public EduEco.Application.Abstractions.Persistence.PageRequest ToPageRequest() => new(Page, PageSize);
}
