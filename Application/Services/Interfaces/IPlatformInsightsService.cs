using Pointer.Application.DTOs.Stats;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IPlatformInsightsService
{
    /// <summary>Cross-tenant view. Caller must be super admin.</summary>
    Task<Result<PlatformInsightsResponse>> GetPlatformInsightsAsync();

    /// <summary>Own-tenant view — relies on the EF query filters already scoping every repository
    /// call to the caller's tenant (a super admin calling this gets the all-tenants view, same as
    /// any other tenant-scoped read).</summary>
    Task<Result<WorkspaceInsightsResponse>> GetWorkspaceInsightsAsync();

    /// <summary>Anonymous, anonymized, thresholded — no auth check (the controller is
    /// <c>[AllowAnonymous]</c>); always returns Success.</summary>
    Task<Result<PublicStatsResponse>> GetPublicStatsAsync();
}
