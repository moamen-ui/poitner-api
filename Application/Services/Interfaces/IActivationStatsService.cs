using Pointer.Application.DTOs.Stats;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-15: the activation funnel as data (demo → converted → widget installed → first comment →
/// first apply, F4). Definitions live in <see cref="Common.UsageEventTypes"/> and DB-15 §3.1.
/// </summary>
public interface IActivationStatsService
{
    /// <summary>Cross-workspace funnel + weekly activated series. Super admins only (the
    /// controller enforces the policy; the service re-checks). <paramref name="weeks"/> 1–52,
    /// default 12.</summary>
    Task<Result<ActivationFunnelResponse>> GetFunnelAsync(int weeks = 12);

    /// <summary>The caller's own workspace checklist (Admin; TryRequireOwner, DB-11a §3.6).</summary>
    Task<Result<WorkspaceActivationResponse>> GetActivationAsync();
}
