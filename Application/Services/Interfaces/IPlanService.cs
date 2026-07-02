using Pointer.Application.DTOs.Plan;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IPlanService
{
    /// <summary>All plans incl. Hidden/Inactive (super-admin).</summary>
    Task<Result<List<PlanAdminResponse>>> ListAsync();

    Task<Result<PlanAdminResponse>> CreateAsync(PlanWriteDto request);
    Task<Result<PlanAdminResponse>> UpdateAsync(int id, PlanWriteDto request);

    /// <summary>Soft-delete. Blocked for Free (fallback) or any plan with active subscriptions.</summary>
    Task<Result> DeleteAsync(int id);

    /// <summary>Marketing-only, DisplayState != Hidden, ordered by SortOrder (anonymous).</summary>
    Task<Result<List<PlanPublicResponse>>> ListPublicAsync();
}
