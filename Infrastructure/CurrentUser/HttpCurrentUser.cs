using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.CurrentUser;

public class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id =>
        Guid.TryParse(
            accessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? accessor.HttpContext?.User.FindFirst("sub")?.Value,
            out var g
        )
            ? g
            : null;

    public bool IsAdmin => accessor.HttpContext?.User.FindFirst("is_admin")?.Value == "true";
}
