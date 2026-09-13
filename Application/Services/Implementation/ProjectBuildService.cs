using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Build;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Turns "this build is live" into "these comments are live".
///
/// The point is the last mile of the feedback loop: a stakeholder wants to know their comment is
/// actually visible on the site, not merely that someone committed a fix. Applied and deployed are
/// different states, sometimes days apart, and only this can tell them apart.
/// </summary>
public class ProjectBuildService : IProjectBuildService
{
    private const int MaxContainedShas = 200;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IProjectService _projectService;
    private readonly ICurrentUser _currentUser;

    public ProjectBuildService(IUnitOfWork unitOfWork, IProjectService projectService, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _currentUser = currentUser;
    }

    private static string? Normalise(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        var trimmed = sha.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[0-9a-f]{7,40}$") ? trimmed : null;
    }

    public async Task<Result<ReportBuildResponse>> ReportAsync(string projectKey, ReportBuildRequest request)
    {
        // Marking work as deployed is a lifecycle action, and a quick-access client is a
        // stakeholder who comments — not someone who moves work along. Same guard as
        // CommentService.UpdateStatusAsync.
        if (_currentUser.IsQuickAccess)
            return Result<ReportBuildResponse>.Forbidden(MessageKeys.Comment.QuickAccessCannotChangeStatus);

        var sha = Normalise(request.Sha);
        if (sha is null)
            return Result<ReportBuildResponse>.Failure(MessageKeys.Build.ShaInvalid);

        var contained = new List<string>();
        if (request.ContainsCommitShas is { Count: > 0 })
        {
            if (request.ContainsCommitShas.Count > MaxContainedShas)
                return Result<ReportBuildResponse>.Failure(MessageKeys.Build.TooManyShas);

            foreach (var candidate in request.ContainsCommitShas)
            {
                var normalised = Normalise(candidate);
                // One bad entry fails the call rather than being dropped. Silently ignoring it
                // would report fewer comments deployed than the caller asked about, and the caller
                // has no way to notice.
                if (normalised is null)
                    return Result<ReportBuildResponse>.Failure(MessageKeys.Build.ShaInvalid);
                contained.Add(normalised);
            }
        }

        // Tenant-filtered: an unknown or foreign key is a 404 here, so a caller cannot use this to
        // probe which project keys exist in other workspaces.
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return Result<ReportBuildResponse>.NotFound(MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null);

        if (project is null)
            return Result<ReportBuildResponse>.NotFound(MessageKeys.Project.NotFound);

        var now = DateTime.UtcNow;

        var existing = await _unitOfWork.Repository<ProjectBuild>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.ProjectId == projectId && b.Sha == sha && b.DeletedAt == null);

        var firstSeen = existing is null;
        if (firstSeen)
        {
            await _unitOfWork.Repository<ProjectBuild>().AddAsync(new ProjectBuild
            {
                ProjectId = projectId,
                Sha = sha,
                FirstSeenAt = now,
                Source = contained.Count > 0 ? BuildSource.Cli : BuildSource.Widget,
                // From the PROJECT's owner, never the caller: the caller is often a member rather
                // than the tenant owner, and stamping their id would drift the row away from the
                // project it describes.
                OwnerId = project.OwnerId,
            });
        }

        // The shas this build is known to carry. The widget cannot compute ancestry, so its report
        // can only match an exact commit — which is why the CLI path exists and is primary.
        var targets = contained.Count > 0 ? contained : new List<string> { sha };

        var newlyDeployed = await _unitOfWork.Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .Where(c => c.ProjectId == projectId
                        && c.DeletedAt == null
                        && c.Status == CommentStatus.Applied
                        // Write-once. A later build must not move DeployedAt, or "when did this go
                        // live" would answer with the most recent deploy instead of the first one
                        // that actually carried the fix.
                        && c.DeployedAt == null
                        && c.CommitSha != null
                        && targets.Contains(c.CommitSha))
            .ToListAsync();

        foreach (var comment in newlyDeployed)
        {
            comment.DeployedAt = now;
            comment.DeployedSha = sha;
            _unitOfWork.Repository<Comment>().Update(comment);
        }

        await _unitOfWork.SaveChangesAsync();

        return Result<ReportBuildResponse>.Success(new ReportBuildResponse
        {
            Sha = sha,
            FirstSeen = firstSeen,
            DeployedCommentIds = newlyDeployed.Select(c => c.Id).OrderBy(id => id).ToList(),
        }, MessageKeys.Build.Reported);
    }
}
