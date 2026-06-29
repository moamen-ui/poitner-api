using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/statuses")]
[Tags("Statuses")]
[AllowAnonymous]
public class StatusesController(IStatusCatalogService statusCatalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<StatusItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get() => Ok(await statusCatalog.GetAllAsync());
}
