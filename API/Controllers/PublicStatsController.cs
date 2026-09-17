using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Anonymous, anonymized, thresholded usage stats for the landing page. Cached for an hour — both
/// server-side (<see cref="IMemoryCache"/>, so a burst of anonymous hits does the aggregation once)
/// and via <see cref="ResponseCacheAttribute"/> (Cache-Control headers for any CDN/browser in front).
/// </summary>
[ApiController]
[Route("api/public/stats")]
[AllowAnonymous]
[Tags("Stats")]
public class PublicStatsController(IPlatformInsightsService platformInsightsService, IMemoryCache cache) : ControllerBase
{
    private const string CacheKey = "public-stats:v1";

    // Coalesces concurrent misses: the aggregation scans every comment across all tenants, and
    // this endpoint is anonymous, so a burst right after the TTL expires must compute it once.
    private static readonly SemaphoreSlim Refresh = new(1, 1);

    [HttpGet]
    [ResponseCache(Duration = 3600)]
    [ProducesResponseType(typeof(PublicStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        if (cache.TryGetValue(CacheKey, out PublicStatsResponse? cached) && cached != null)
            return Ok(Result<PublicStatsResponse>.Success(cached));

        await Refresh.WaitAsync(HttpContext.RequestAborted);
        try
        {
            // Re-check: whoever held the semaphore before us has probably just filled the cache.
            if (cache.TryGetValue(CacheKey, out cached) && cached != null)
                return Ok(Result<PublicStatsResponse>.Success(cached));

            var result = await platformInsightsService.GetPublicStatsAsync();
            if (result.IsSuccess && result.Data != null)
                cache.Set(CacheKey, result.Data, TimeSpan.FromHours(1));

            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }
        finally
        {
            Refresh.Release();
        }
    }
}
