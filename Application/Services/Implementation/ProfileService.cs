using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Profile;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class ProfileService : IProfileService
{
    private readonly IUnitOfWork _unitOfWork;
    public ProfileService(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    public async Task<Result<UserProfileResponse>> GetByIdAsync(int userId)
    {
        var user = await _unitOfWork.Repository<User>().Query().AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        return user is null
            ? Result<UserProfileResponse>.NotFound("User not found")
            : await BuildAsync(user);
    }

    public async Task<Result<UserProfileResponse>> GetByPublicIdAsync(Guid publicId)
    {
        var user = await _unitOfWork.Repository<User>().Query().AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.PublicId == publicId && u.DeletedAt == null);
        return user is null
            ? Result<UserProfileResponse>.NotFound("User not found")
            : await BuildAsync(user);
    }

    private async Task<Result<UserProfileResponse>> BuildAsync(User user)
    {
        var pid = user.PublicId;

        // comments grouped by (project, environment, status)
        var comments = await _unitOfWork.Repository<Comment>().Query().AsNoTracking()
            .Where(c => c.AuthorId == pid && c.DeletedAt == null)
            .GroupBy(c => new { c.ProjectId, c.Environment, c.Status })
            .Select(g => new { g.Key.ProjectId, g.Key.Environment, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        // replies grouped by (project, environment) via the parent comment
        var replies = await _unitOfWork.Repository<Reply>().Query().AsNoTracking()
            .Where(r => r.AuthorId == pid && r.DeletedAt == null && r.Comment.DeletedAt == null)
            .GroupBy(r => new { r.Comment.ProjectId, r.Comment.Environment })
            .Select(g => new { g.Key.ProjectId, g.Key.Environment, Count = g.Count() })
            .ToListAsync();

        var projectIds = comments.Select(c => c.ProjectId)
            .Concat(replies.Select(r => r.ProjectId)).Distinct().ToList();

        var projects = await _unitOfWork.Repository<Project>().Query().AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Key, p.Name, p.IsActive })
            .ToListAsync();

        void Apply(ProfileCounts t, CommentStatus s, int n)
        {
            t.Comments += n;
            switch (s)
            {
                case CommentStatus.Open: t.Open += n; break;
                case CommentStatus.ReadyToApply: t.ReadyToApply += n; break;
                case CommentStatus.Applied: t.Applied += n; break;
                case CommentStatus.Archived: t.Archived += n; break;
            }
        }

        var perProject = new List<ProfileProject>();
        foreach (var p in projects)
        {
            var proj = new ProfileProject { ProjectId = p.Id, Key = p.Key, Name = p.Name, IsActive = p.IsActive };
            var envIds = comments.Where(c => c.ProjectId == p.Id).Select(c => c.Environment)
                .Concat(replies.Where(r => r.ProjectId == p.Id).Select(r => r.Environment))
                .Distinct();
            foreach (var envId in envIds)
            {
                var env = new ProfileEnvironment { Environment = (int)envId };
                foreach (var row in comments.Where(c => c.ProjectId == p.Id && c.Environment == envId))
                    Apply(env, row.Status, row.Count);
                env.Replies = replies.Where(r => r.ProjectId == p.Id && r.Environment == envId).Sum(r => r.Count);
                proj.Environments.Add(env);
            }
            // project-level rollup
            foreach (var e in proj.Environments)
            {
                proj.Comments += e.Comments; proj.Open += e.Open; proj.ReadyToApply += e.ReadyToApply;
                proj.Applied += e.Applied; proj.Archived += e.Archived; proj.Replies += e.Replies;
            }
            proj.Environments = proj.Environments.OrderBy(e => e.Environment).ToList();
            perProject.Add(proj);
        }
        perProject = perProject.OrderByDescending(p => p.Comments + p.Replies).ToList();

        var totals = new ProfileTotals { ProjectsInvolved = perProject.Count };
        foreach (var p in perProject)
        {
            totals.Comments += p.Comments; totals.Open += p.Open; totals.ReadyToApply += p.ReadyToApply;
            totals.Applied += p.Applied; totals.Archived += p.Archived; totals.Replies += p.Replies;
        }

        return Result<UserProfileResponse>.Success(new UserProfileResponse
        {
            User = new ProfileUser { Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, RoleName = user.Role?.Name ?? string.Empty },
            Totals = totals,
            Projects = perProject,
        });
    }
}
