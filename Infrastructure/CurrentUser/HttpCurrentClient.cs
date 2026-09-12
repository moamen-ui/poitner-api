using Microsoft.AspNetCore.Http;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.CurrentUser;

public class HttpCurrentClient(IHttpContextAccessor accessor) : ICurrentClient
{
    private const string HeaderName = "X-Pointer-Client";

    public bool IsHumanSurface
    {
        get
        {
            var value = accessor.HttpContext?.Request.Headers[HeaderName].ToString();
            return string.Equals(value, "dashboard", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "widget", StringComparison.OrdinalIgnoreCase);
        }
    }
}
