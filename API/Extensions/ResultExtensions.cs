using Microsoft.AspNetCore.Mvc;
using Pointer.Application.Response;

namespace Pointer.API.Extensions;

/// <summary>
/// Maps a <see cref="Result"/> / <see cref="Result{T}"/> to the matching <see cref="IActionResult"/>
/// status code while preserving the Result body in the response. Mirrors the flag->status mapping
/// already used inline across controllers (IsNotFound->404, IsConflict->409, IsForbidden->403,
/// IsSuccess->200, else 400). Provided for future controller adoption; not retrofitted everywhere.
/// </summary>
public static class ResultExtensions
{
    public static IActionResult ToActionResult(this Result r) =>
        r.IsNotFound ? new NotFoundObjectResult(r)
        : r.IsConflict ? new ConflictObjectResult(r)
        : r.IsForbidden ? new ObjectResult(r) { StatusCode = StatusCodes.Status403Forbidden }
        : r.IsSuccess ? new OkObjectResult(r)
        : new BadRequestObjectResult(r);

    public static IActionResult ToActionResult<T>(this Result<T> r) =>
        r.IsNotFound ? new NotFoundObjectResult(r)
        : r.IsConflict ? new ConflictObjectResult(r)
        : r.IsForbidden ? new ObjectResult(r) { StatusCode = StatusCodes.Status403Forbidden }
        : r.IsSuccess ? new OkObjectResult(r)
        : new BadRequestObjectResult(r);
}
