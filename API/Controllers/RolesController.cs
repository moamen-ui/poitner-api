using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Public, anonymous-safe roles endpoint for the signup / re-apply dropdowns.
/// Returns only active, NON-admin roles — never exposes admin-granting roles.
/// </summary>
[ApiController]
[Route("api/roles")]
public class RolesController(IRoleService roleService) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var result = await roleService.ListPublicAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
