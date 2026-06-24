using Pointer.Application.DTOs.Stats;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IStatsService
{
    Task<Result<StatsResponse>> GetAsync();
}
