using Pointer.Application.DTOs.Auth;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// Browser-based ("device code") sign-in for the CLI — see <see cref="Pointer.Domain.Entity.DeviceLogin"/>
/// for the full flow. Every method here is reachable anonymously except <see cref="GetInfoAsync"/>,
/// <see cref="ApproveAsync"/> and <see cref="DenyAsync"/>, which require a signed-in dashboard user
/// who is not a super admin.
/// </summary>
public interface IDeviceLoginService
{
    /// <summary>Mints a fresh (deviceCode, userCode) pair. Also opportunistically deletes rows more
    /// than a day past expiry — cheap housekeeping instead of a background job.</summary>
    Task<Result<DeviceLoginStartResponse>> StartAsync(DeviceLoginStartRequest request);

    /// <summary>Looked up by the SHA-256 of the raw device code. Returns the raw API key exactly
    /// once — the first poll after approval — then marks the row Consumed.</summary>
    Task<Result<DeviceLoginPollResponse>> PollAsync(DeviceLoginPollRequest request);

    /// <summary>For the dashboard's /cli-login page: what this code is asking for, before deciding.</summary>
    Task<Result<DeviceLoginInfoResponse>> GetInfoAsync(string userCode);

    Task<Result<DeviceLoginInfoResponse>> ApproveAsync(string userCode);

    Task<Result<DeviceLoginInfoResponse>> DenyAsync(string userCode);
}
