using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class StatsService : IStatsService
{
    private readonly IUnitOfWork _unitOfWork;

    public StatsService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<StatsResponse>> GetAsync()
    {
        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Select(p => new { p.Id, p.Key, p.Name, p.IsActive })
            .ToListAsync();

        var usersCount = await _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .CountAsync(u => u.DeletedAt == null);

        var pendingUsersCount = await _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .CountAsync(u => u.DeletedAt == null && u.ApprovalStatus == ApprovalStatus.Pending);

        // One grouped query: comment counts per (project, status).
        var grouped = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .GroupBy(c => new { c.ProjectId, c.Status, c.IsPrivate })
            .Select(g => new { g.Key.ProjectId, g.Key.Status, g.Key.IsPrivate, Count = g.Count() })
            .ToListAsync();

        var perProject = projects
            .Select(p =>
            {
                var rows = grouped.Where(g => g.ProjectId == p.Id).ToList();
                var open = rows.Where(r => r.Status == CommentStatus.Open).Sum(r => r.Count);
                var pending = rows.Where(r => r.Status == CommentStatus.ReadyToApply).Sum(r => r.Count);
                var completed = rows.Where(r => r.Status == CommentStatus.Applied).Sum(r => r.Count);
                var privateComments = rows.Where(r => r.IsPrivate).Sum(r => r.Count);
                return new ProjectStats
                {
                    ProjectId = p.Id,
                    Key = p.Key,
                    Name = p.Name,
                    IsActive = p.IsActive,
                    Open = open,
                    Pending = pending,
                    Completed = completed,
                    Comments = open + pending + completed,
                    PrivateComments = privateComments,
                };
            })
            .OrderByDescending(p => p.Comments)
            .ToList();

        var totals = new StatsTotals
        {
            Projects = projects.Count,
            Users = usersCount,
            PendingUsers = pendingUsersCount,
            Open = grouped.Where(g => g.Status == CommentStatus.Open).Sum(g => g.Count),
            Pending = grouped.Where(g => g.Status == CommentStatus.ReadyToApply).Sum(g => g.Count),
            Completed = grouped.Where(g => g.Status == CommentStatus.Applied).Sum(g => g.Count),
            PrivateComments = grouped.Where(g => g.IsPrivate).Sum(g => g.Count),
        };
        totals.Comments = totals.Open + totals.Pending + totals.Completed;

        return Result<StatsResponse>.Success(new StatsResponse { Totals = totals, Projects = perProject });
    }
}
