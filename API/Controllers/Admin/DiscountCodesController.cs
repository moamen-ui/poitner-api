using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.DiscountCode;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>DB-20 §3.9/§3.6h. Super-admin reference/discount code CRUD.</summary>
[ApiController]
[Route("api/admin/discount-codes")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("DiscountCodes")]
[Produces("application/json")]
public class DiscountCodesController(IDiscountCodeService discountCodes) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<DiscountCodeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? sort, [FromQuery] bool? active)
    {
        var result = await discountCodes.ListAsync(sort, active);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(DiscountCodeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id)
    {
        var result = await discountCodes.GetAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [Audited(AuditActions.DiscountCodeCreated)]
    [ProducesResponseType(typeof(DiscountCodeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateDiscountCodeRequest request)
    {
        var result = await discountCodes.CreateAsync(request);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [Audited(AuditActions.DiscountCodeUpdated)]
    [ProducesResponseType(typeof(DiscountCodeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateDiscountCodeRequest request)
    {
        var result = await discountCodes.UpdateAsync(id, request);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:int}/redemptions")]
    [ProducesResponseType(typeof(List<DiscountRedemptionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRedemptions(int id)
    {
        var result = await discountCodes.GetRedemptionsAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
