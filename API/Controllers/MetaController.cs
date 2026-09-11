using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Pointer.API.Extensions;
using Pointer.Application.DTOs.Meta;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/meta")]
[AllowAnonymous]
[Produces("application/json")]
[EnableRateLimiting("meta")]
[Tags("Meta")]
public class MetaController(IMetaService metaService, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(MetaResponse), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get()
    {
        var publicBase = PointerUrlResolver.ResolvePublicUrl(configuration, Request);
        var result = await metaService.GetAsync(publicBase);
        return Ok(Result<MetaResponse>.Success(result));
    }
}
