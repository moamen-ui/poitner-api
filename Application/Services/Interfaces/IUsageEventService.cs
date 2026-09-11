using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IUsageEventService
{
    Task<Result<bool>> RecordEventAsync(string type, string source, string? projectKey = null, object? meta = null);
    Task<EventsSummaryResponse> GetSummaryAsync(int projectId);
}

public class EventsSummaryResponse
{
    public Dictionary<string, int> Counts { get; set; } = new();
    public Dictionary<string, DateTime> FirstAt { get; set; } = new();
}
