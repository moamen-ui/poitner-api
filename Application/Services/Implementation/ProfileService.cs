using System.Security.Cryptography;
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
    private readonly IApiKeyService _apiKeys;
    private readonly ICurrentUser _currentUser;

    public ProfileService(IUnitOfWork unitOfWork, IApiKeyService apiKeys, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _apiKeys = apiKeys;
        _currentUser = currentUser;
    }

    public async Task<Result<UserProfileResponse>> GetByIdAsync(int userId)
    {
        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        return user is null
            ? Result<UserProfileResponse>.NotFound("User not found")
            : await BuildAsync(user);
    }

    public async Task<Result<UserProfileResponse>> GetByPublicIdAsync(Guid publicId)
    {
        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .AsNoTracking()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.PublicId == publicId && u.DeletedAt == null);
        return user is null
            ? Result<UserProfileResponse>.NotFound("User not found")
            : await BuildAsync(user);
    }

    public async Task<Result<ApiKeyResponse>> GetOrCreateApiKeyAsync(
        Guid publicId,
        Guid? workspaceId
    ) => ToResponse(await _apiKeys.GetOrCreateAsync(publicId, workspaceId));

    public async Task<Result<ApiKeyResponse>> RegenerateApiKeyAsync(
        Guid publicId,
        Guid? workspaceId
    ) => ToResponse(await _apiKeys.RegenerateAsync(publicId, workspaceId));

    /// <summary>
    /// A key that exists but cannot be decrypted (the encryption key was rotated, or the blob was
    /// tampered with) is reported as undisplayable. It is deliberately NOT treated as missing:
    /// regenerating here would silently invalidate every developer's stored key the first time
    /// someone opened their profile page after a key change. Logins keep working throughout —
    /// they match on the hash, which never needed the encryption key.
    /// </summary>
    private static Result<ApiKeyResponse> ToResponse(ApiKeyResult result)
    {
        if (!result.Found)
            return Result<ApiKeyResponse>.NotFound("User not found");

        if (result.RawKey is null)
            return Result<ApiKeyResponse>.Failure(
                "Key display unavailable — the encryption key changed. Regenerate to get a new key."
            );

        return Result<ApiKeyResponse>.Success(
            new ApiKeyResponse
            {
                ApiKey = result.RawKey,
                Prefix = result.Prefix,
                LastUsedAt = result.LastUsedAt,
            }
        );
    }

    private async Task<Result<UserProfileResponse>> BuildAsync(User user)
    {
        var pid = user.PublicId;

        // DB-13 (F2): Comment/Reply lost their unconditional super-admin filter branch — a plain
        // operator's profile query would otherwise silently drop to zero counts across every
        // workspace. `wide` restores the cross-workspace view for a plain operator (counts and
        // project key/name only — no body: metadata); an impersonating operator is pinned by the
        // filter itself (TenantId == the target workspace) and needs no override.
        var wide = _currentUser.IsSuperAdmin && !_currentUser.IsImpersonating;

        // comments grouped by (project, environment, status)
        var commentsQuery = _unitOfWork.Repository<Comment>().Query().AsNoTracking();
        if (wide)
            commentsQuery = commentsQuery.IgnoreQueryFilters();
        var comments = await commentsQuery
            .Where(c => c.AuthorId == pid && c.DeletedAt == null)
            .GroupBy(c => new
            {
                c.ProjectId,
                c.Environment,
                c.Status,
            })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.Environment,
                g.Key.Status,
                Count = g.Count(),
            })
            .ToListAsync();

        // replies grouped by (project, environment) via the parent comment
        var repliesQuery = _unitOfWork.Repository<Reply>().Query().AsNoTracking();
        if (wide)
            repliesQuery = repliesQuery.IgnoreQueryFilters();
        var replies = await repliesQuery
            .Where(r => r.AuthorId == pid && r.DeletedAt == null && r.Comment.DeletedAt == null)
            .GroupBy(r => new { r.Comment.ProjectId, r.Comment.Environment })
            .Select(g => new
            {
                g.Key.ProjectId,
                g.Key.Environment,
                Count = g.Count(),
            })
            .ToListAsync();

        var projectIds = comments
            .Select(c => c.ProjectId)
            .Concat(replies.Select(r => r.ProjectId))
            .Distinct()
            .ToList();

        var projectsQuery = _unitOfWork.Repository<Project>().Query().AsNoTracking();
        if (wide)
            projectsQuery = projectsQuery.IgnoreQueryFilters();
        var projects = await projectsQuery
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Key,
                p.Name,
                p.IsActiveLocal,
                p.IsActiveStaging,
                p.IsActiveProduction,
            })
            .ToListAsync();

        void Apply(ProfileCounts t, CommentStatus s, int n)
        {
            t.Comments += n;
            switch (s)
            {
                case CommentStatus.Open:
                    t.Open += n;
                    break;
                case CommentStatus.ReadyToApply:
                    t.ReadyToApply += n;
                    break;
                case CommentStatus.Applied:
                    t.Applied += n;
                    break;
                case CommentStatus.Archived:
                    t.Archived += n;
                    break;
            }
        }

        var perProject = new List<ProfileProject>();
        foreach (var p in projects)
        {
            var proj = new ProfileProject
            {
                ProjectId = p.Id,
                Key = p.Key,
                Name = p.Name,
                // Coarse summary for a profile page — active in at least one environment.
                IsActive = p.IsActiveLocal || p.IsActiveStaging || p.IsActiveProduction,
            };
            var envIds = comments
                .Where(c => c.ProjectId == p.Id)
                .Select(c => c.Environment)
                .Concat(replies.Where(r => r.ProjectId == p.Id).Select(r => r.Environment))
                .Distinct();
            foreach (var envId in envIds)
            {
                var env = new ProfileEnvironment { Environment = (int)envId };
                foreach (
                    var row in comments.Where(c => c.ProjectId == p.Id && c.Environment == envId)
                )
                    Apply(env, row.Status, row.Count);
                env.Replies = replies
                    .Where(r => r.ProjectId == p.Id && r.Environment == envId)
                    .Sum(r => r.Count);
                proj.Environments.Add(env);
            }
            // project-level rollup
            foreach (var e in proj.Environments)
            {
                proj.Comments += e.Comments;
                proj.Open += e.Open;
                proj.ReadyToApply += e.ReadyToApply;
                proj.Applied += e.Applied;
                proj.Archived += e.Archived;
                proj.Replies += e.Replies;
            }
            proj.Environments = proj.Environments.OrderBy(e => e.Environment).ToList();
            perProject.Add(proj);
        }
        perProject = perProject.OrderByDescending(p => p.Comments + p.Replies).ToList();

        var totals = new ProfileTotals { ProjectsInvolved = perProject.Count };
        foreach (var p in perProject)
        {
            totals.Comments += p.Comments;
            totals.Open += p.Open;
            totals.ReadyToApply += p.ReadyToApply;
            totals.Applied += p.Applied;
            totals.Archived += p.Archived;
            totals.Replies += p.Replies;
        }

        return Result<UserProfileResponse>.Success(
            new UserProfileResponse
            {
                User = new ProfileUser
                {
                    Id = user.Id,
                    DisplayName = user.DisplayName,
                    Email = user.Email,
                    RoleName = user.Role?.Name ?? string.Empty,
                },
                Totals = totals,
                Projects = perProject,
            }
        );
    }
}
