using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class UsageEventService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IProjectService projectService) : IUsageEventService
{
    public async Task<Result<bool>> RecordEventAsync(string type, string source, string? projectKey = null, object? meta = null)
    {
        int? projectId = null;
        Guid? ownerId = currentUser.TenantId;

        if (!string.IsNullOrEmpty(projectKey))
        {
            var pResult = await projectService.EnsureAsync(projectKey);
            if (!pResult.IsSuccess)
            {
                return Result<bool>.NotFound("Project not found");
            }
            projectId = pResult.Data;
            
            var project = await unitOfWork.Repository<Project>().GetByIdAsync(projectId.Value);
            if (project != null)
            {
                ownerId = project.OwnerId;
            }
        }

        var metaJson = meta != null ? JsonSerializer.Serialize(meta) : null;
        if (metaJson?.Length > 2000)
        {
            return Result<bool>.Failure("Meta data exceeds max length of 2000 characters");
        }

        var ev = new UsageEvent
        {
            Type = type,
            Source = source,
            ProjectId = projectId,
            OwnerId = ownerId,
            UserId = currentUser.Id,
            Meta = metaJson,
            CreatedAt = DateTime.UtcNow
        };

        unitOfWork.UsageEvents.Add(ev);
        await unitOfWork.SaveChangesAsync();
        
        return Result<bool>.Success(true);
    }

    public async Task<EventsSummaryResponse> GetSummaryAsync(int projectId)
    {
        var events = await unitOfWork.UsageEvents
            .Where(e => e.ProjectId == projectId)
            .GroupBy(e => e.Type)
            .Select(g => new
            {
                Type = g.Key,
                Count = g.Count(),
                FirstAt = g.Min(e => e.CreatedAt)
            })
            .ToListAsync();

        var response = new EventsSummaryResponse();
        foreach (var ev in events)
        {
            response.Counts[ev.Type] = ev.Count;
            response.FirstAt[ev.Type] = ev.FirstAt;
        }

        return response;
    }
}
