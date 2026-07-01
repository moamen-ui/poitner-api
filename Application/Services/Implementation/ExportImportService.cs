using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Export;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Implementation;

public class ExportImportService : IExportImportService
{
    // --- schema versioning contract (see plan §5) ---
    public const string CurrentSchemaVersion = "1.0";
    private static readonly int[] SupportedMajorVersions = [1];

    // --- import limits (Open Decision #4: hard-coded for v1) ---
    private const int MaxImportCommentCount = 5000;
    private const int MaxRepliesPerComment = 500;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IProjectService _projectService;
    private readonly ICurrentUser _currentUser;
    private readonly ISettingsService _settings;

    public ExportImportService(
        IUnitOfWork unitOfWork,
        IProjectService projectService,
        ICurrentUser currentUser,
        ISettingsService settings
    )
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _currentUser = currentUser;
        _settings = settings;
    }

    // ===========================================================================
    // EXPORT
    // ===========================================================================

    public async Task<Result<ExportFileDto>> ExportProjectAsync(string projectKey, ExportOptions options)
    {
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<ExportFileDto>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<ExportFileDto>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var comments = await QueryCommentsAsync(options, projectId: projectResult.Data);
        var dto = await BuildExportFileAsync(comments, sourceProject: projectKey);
        return Result<ExportFileDto>.Success(dto, MessageKeys.ExportImport.Exported);
    }

    public async Task<Result<ExportFileDto>> ExportWorkspaceAsync(ExportOptions options)
    {
        var comments = await QueryCommentsAsync(options, projectId: null);
        var dto = await BuildExportFileAsync(comments, sourceProject: null);
        return Result<ExportFileDto>.Success(dto, MessageKeys.ExportImport.Exported);
    }

    private async Task<List<Comment>> QueryCommentsAsync(ExportOptions options, int? projectId)
    {
        // IncludePrivate / IncludeDeleted require admin — clamped server-side regardless of input.
        var includePrivate = options.IncludePrivate && _currentUser.IsAdmin;
        var includeDeleted = options.IncludeDeleted && _currentUser.IsAdmin;
        var callerId = _currentUser.Id ?? Guid.Empty;

        // Type the variable as IQueryable<Comment> so subsequent .Where() reassignments compile;
        // the .Include(...) still applies (IIncludableQueryable is an IQueryable<Comment>).
        IQueryable<Comment> query = _unitOfWork
            .Repository<Comment>()
            .Query()
            .AsNoTracking()
            // Replies load under the same tenant filter (global query filter applies).
            .Include(c => c.Replies);

        if (projectId.HasValue)
            query = query.Where(c => c.ProjectId == projectId.Value);

        // Tenant isolation comes from the EF global query filter on Comment (OwnerId == tenant).
        // Deleted rows are filtered explicitly here (NOT via the global filter) unless IncludeDeleted.
        if (!includeDeleted)
            query = query.Where(c => c.DeletedAt == null);

        if (options.Status.HasValue)
            query = query.Where(c => c.Status == options.Status.Value);

        if (options.Environment.HasValue)
            query = query.Where(c => c.Environment == options.Environment.Value);

        // Private comments are only returned to their author (matches CommentService.ListAsync;
        // admins get no bypass either, unless IncludePrivate+IsAdmin is requested).
        if (!includePrivate)
            query = query.Where(c => !c.IsPrivate || c.AuthorId == callerId);

        return await query.OrderBy(c => c.CreatedAt).ToListAsync();
    }

    private async Task<ExportFileDto> BuildExportFileAsync(List<Comment> comments, string? sourceProject)
    {
        var names = await ResolveNamesAsync(comments.SelectMany(AuthorIds));

        // Workspace exports carry each comment's own project_key (resolved from ProjectId).
        Dictionary<int, string>? projectKeys = null;
        if (sourceProject == null && comments.Count > 0)
        {
            var ids = comments.Select(c => c.ProjectId).Distinct().ToList();
            projectKeys = await _unitOfWork
                .Repository<Project>()
                .Query()
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Key);
        }

        var commentDtos = new List<CommentExportDto>(comments.Count);
        var replySeq = 0;
        for (var i = 0; i < comments.Count; i++)
        {
            var c = comments[i];
            var liveReplies = c.Replies.Where(r => r.DeletedAt == null).ToList();
            commentDtos.Add(
                new CommentExportDto
                {
                    ExportId = $"c-{i + 1}",
                    ProjectKey = sourceProject ?? projectKeys?.GetValueOrDefault(c.ProjectId),
                    Body = c.Body,
                    Environment = c.Environment.ToString(),
                    Status = c.Status.ToString(),
                    IsPrivate = c.IsPrivate,
                    CreatedAt = c.CreatedAt,
                    AppliedAt = c.AppliedAt,
                    AppliedByLabel = c.AppliedByLabel,
                    EditedAt = c.EditedAt,
                    AuthorDisplayName = names.GetValueOrDefault(c.AuthorId),
                    AppliedByDisplayName = c.AppliedBy.HasValue
                        ? names.GetValueOrDefault(c.AppliedBy.Value)
                        : null,
                    EditedByDisplayName = c.EditedBy.HasValue
                        ? names.GetValueOrDefault(c.EditedBy.Value)
                        : null,
                    Element = MapElementForExport(c.Element),
                    Replies = liveReplies
                        .Select(r => new ReplyExportDto
                        {
                            ExportId = $"r-{++replySeq}",
                            Body = r.Body,
                            AuthorDisplayName = names.GetValueOrDefault(r.AuthorId),
                            CreatedAt = r.CreatedAt
                        })
                        .ToList()
                }
            );
        }

        return new ExportFileDto
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAt = DateTime.UtcNow,
            SourceProject = sourceProject,
            SourceServer = null, // informational; left null (controller sets Content-Disposition)
            Comments = commentDtos
        };
    }

    private static ElementCaptureExportDto MapElementForExport(ElementCapture e) =>
        new()
        {
            Selector = e.Selector,
            Snapshot = e.Snapshot,
            Classes = e.Classes,
            ComputedStyles = e.ComputedStyles,
            AppliedCssRules = e.AppliedCssRules,
            SourcePath = e.SourcePath,
            ParentInfo = e.ParentInfo,
            // Screenshots are NEVER transferred (plan §3.3): null the URL and flag the omission.
            ScreenshotUrl = null,
            ScreenshotOmitted = !string.IsNullOrEmpty(e.ScreenshotUrl),
            PageUrl = e.PageUrl,
            Route = e.Route,
            PageTitle = e.PageTitle,
            ViewportWidth = e.ViewportWidth,
            ViewportHeight = e.ViewportHeight,
            DeviceType = e.DeviceType,
            DevicePixelRatio = e.DevicePixelRatio
        };

    // ===========================================================================
    // IMPORT
    // ===========================================================================

    public async Task<Result<ImportResultDto>> ImportProjectAsync(string projectKey, ExportFileDto file)
    {
        var validation = ValidateFile(file);
        if (validation != null)
            return Result<ImportResultDto>.Failure(validation);

        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<ImportResultDto>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<ImportResultDto>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var (projectId, projectOwnerId) = await ResolveProjectAsync(projectResult.Data);
        var capError = await CheckDemoCapAsync(projectOwnerId, file.Comments.Count);
        if (capError != null)
            return Result<ImportResultDto>.Failure(capError);

        var warnings = new List<string>();
        var (comments, replies) = await InsertCommentsAsync(
            file.Comments,
            projectId,
            projectOwnerId,
            warnings
        );

        await _unitOfWork.SaveChangesAsync();
        return Result<ImportResultDto>.Success(
            BuildResult(comments, replies, warnings),
            MessageKeys.ExportImport.Imported
        );
    }

    public async Task<Result<ImportResultDto>> ImportWorkspaceAsync(ExportFileDto file)
    {
        var validation = ValidateFile(file);
        if (validation != null)
            return Result<ImportResultDto>.Failure(validation);

        var warnings = new List<string>();
        var totalComments = 0;
        var totalReplies = 0;

        // Route each comment to the project named by its project_key (lazy-created via EnsureAsync).
        foreach (var grouping in file.Comments.GroupBy(c => c.ProjectKey))
        {
            var projectKey = grouping.Key;
            if (string.IsNullOrWhiteSpace(projectKey))
                return Result<ImportResultDto>.Failure(
                    "Comment is missing project_key (required for workspace import)."
                );

            var projectResult = await _projectService.EnsureAsync(projectKey);
            if (!projectResult.IsSuccess)
                return projectResult.IsConflict
                    ? Result<ImportResultDto>.Conflict(
                        projectResult.Message ?? MessageKeys.Project.Disabled
                    )
                    : Result<ImportResultDto>.NotFound(
                        projectResult.Message ?? MessageKeys.Project.NotFound
                    );

            var groupList = grouping.ToList();
            var (projectId, projectOwnerId) = await ResolveProjectAsync(projectResult.Data);
            var capError = await CheckDemoCapAsync(projectOwnerId, groupList.Count);
            if (capError != null)
                return Result<ImportResultDto>.Failure(capError);

            var (c, r) = await InsertCommentsAsync(groupList, projectId, projectOwnerId, warnings);
            totalComments += c;
            totalReplies += r;
        }

        await _unitOfWork.SaveChangesAsync();
        return Result<ImportResultDto>.Success(
            BuildResult(totalComments, totalReplies, warnings),
            MessageKeys.ExportImport.Imported
        );
    }

    /// <summary>Loads the project's OwnerId — the tenant OwnerId all imported rows must be stamped with.</summary>
    private async Task<(int id, Guid ownerId)> ResolveProjectAsync(int projectId)
    {
        var ownerId = await _unitOfWork
            .Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.OwnerId)
            .FirstAsync();
        return (projectId, ownerId ?? Guid.Empty);
    }

    private async Task<(int comments, int replies)> InsertCommentsAsync(
        List<CommentExportDto> dtos,
        int projectId,
        Guid projectOwnerId,
        List<string> warnings
    )
    {
        var importerId = _currentUser.Id ?? Guid.Empty;
        var screenshotOmitted = 0;
        var commentCount = 0;
        var replyCount = 0;

        foreach (var dto in dtos)
        {
            if (dto.Element is { ScreenshotOmitted: true })
                screenshotOmitted++;

            // Re-attribution (plan §4.3): author becomes the importer; the original display name is
            // preserved as a human-readable footnote. AppliedByLabel is kept verbatim (already a label).
            var body = AppendAttribution(dto.Body, dto.AuthorDisplayName);

            var comment = new Comment
            {
                ProjectId = projectId,
                Environment = Enum.TryParse<EnvironmentTag>(dto.Environment, out var env)
                    ? env
                    : EnvironmentTag.Local,
                Status = Enum.TryParse<CommentStatus>(dto.Status, out var st)
                    ? st
                    : CommentStatus.Open,
                AuthorId = importerId, // re-attributed
                Body = body,
                IsPrivate = dto.IsPrivate,
                OwnerId = projectOwnerId, // stamped from the TARGET project, never from the JSON
                AppliedAt = dto.AppliedAt,
                AppliedByLabel = dto.AppliedByLabel,
                // EditedAt/EditedBy are intentionally NOT carried over (import is not an edit).
                Element = MapElementForImport(dto.Element)
            };

            if (dto.CreatedAt != default)
            {
                comment.CreatedAt = dto.CreatedAt;
                _unitOfWork.PreserveCreatedAtOnInsert(comment);
            }

            if (dto.Replies != null)
            {
                foreach (var r in dto.Replies)
                {
                    var reply = new Reply
                    {
                        AuthorId = importerId, // re-attributed
                        Body = AppendAttribution(r.Body, r.AuthorDisplayName),
                        OwnerId = projectOwnerId
                    };
                    if (r.CreatedAt != default)
                    {
                        reply.CreatedAt = r.CreatedAt;
                        _unitOfWork.PreserveCreatedAtOnInsert(reply);
                    }
                    comment.Replies.Add(reply);
                    replyCount++;
                }
            }

            await _unitOfWork.Repository<Comment>().AddAsync(comment);
            commentCount++;
        }

        if (screenshotOmitted > 0)
            warnings.Add(
                $"{screenshotOmitted} comment(s) had screenshots that were omitted (screenshot_omitted=true)."
            );

        return (commentCount, replyCount);
    }

    private static string AppendAttribution(string body, string? originalAuthor) =>
        string.IsNullOrWhiteSpace(originalAuthor)
            ? body
            : $"{body}\n\n*(Imported — originally by: {originalAuthor})*";

    private static ElementCapture MapElementForImport(ElementCaptureExportDto? dto)
    {
        if (dto == null)
            return new ElementCapture();
        return new ElementCapture
        {
            Selector = dto.Selector,
            Snapshot = dto.Snapshot,
            Classes = dto.Classes,
            ComputedStyles = dto.ComputedStyles,
            AppliedCssRules = dto.AppliedCssRules,
            SourcePath = dto.SourcePath,
            ParentInfo = dto.ParentInfo,
            // Never trust a screenshot reference from an import file (plan §4.6 / risk register).
            ScreenshotUrl = null,
            PageUrl = dto.PageUrl,
            Route = dto.Route,
            PageTitle = dto.PageTitle,
            ViewportWidth = dto.ViewportWidth,
            ViewportHeight = dto.ViewportHeight,
            DeviceType = dto.DeviceType,
            DevicePixelRatio = dto.DevicePixelRatio
        };
    }

    private static ImportResultDto BuildResult(int comments, int replies, List<string> warnings) =>
        new()
        {
            ImportedComments = comments,
            ImportedReplies = replies,
            SkippedDuplicates = 0, // dedup skipped for v1 (Open Decision #1)
            Warnings = warnings
        };

    // ---------------------------------------------------------------------------
    // Validation — single fail-fast pass before any DB write (plan §4.6)
    // ---------------------------------------------------------------------------

    private static string? ValidateFile(ExportFileDto file)
    {
        if (file == null)
            return MessageKeys.ExportImport.InvalidJson;

        // Schema major version check.
        if (string.IsNullOrWhiteSpace(file.SchemaVersion))
            return MessageKeys.ExportImport.UnsupportedSchemaVersion;
        var majorText = file.SchemaVersion.Split('.')[0];
        if (
            !int.TryParse(majorText, out var major)
            || !SupportedMajorVersions.Contains(major)
        )
            return $"{MessageKeys.ExportImport.UnsupportedSchemaVersion} Supported: {string.Join(", ", SupportedMajorVersions.Select(v => v + ".x"))}.";

        if (file.Comments == null)
            return MessageKeys.ExportImport.MissingCommentsArray;

        if (file.Comments.Count > MaxImportCommentCount)
            return MessageKeys.ExportImport.TooManyComments;

        var errors = new List<string>();
        foreach (var c in file.Comments)
        {
            if (string.IsNullOrWhiteSpace(c.Body))
                errors.Add($"Comment {c.ExportId}: body is required.");
            if (!Enum.TryParse<EnvironmentTag>(c.Environment, out _))
                errors.Add($"Comment {c.ExportId}: invalid environment '{c.Environment}'.");
            if (!Enum.TryParse<CommentStatus>(c.Status, out _))
                errors.Add($"Comment {c.ExportId}: invalid status '{c.Status}'.");
            if (c.Replies != null && c.Replies.Count > MaxRepliesPerComment)
                errors.Add($"Comment {c.ExportId}: more than {MaxRepliesPerComment} replies.");
        }
        return errors.Count > 0 ? string.Join(" ", errors) : null;
    }

    /// <summary>
    /// Rejects the import early when a demo tenant would exceed its comment cap.
    /// Uses IgnoreQueryFilters to count across the owning tenant exactly as CommentService.CreateAsync.
    /// </summary>
    private async Task<string?> CheckDemoCapAsync(Guid? projectOwnerId, int importCount)
    {
        if (projectOwnerId is not Guid owner)
            return null;

        var demoOwner = await _unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.PublicId == owner && u.IsDemo && u.DeletedAt == null)
            .Select(u => new { u.DemoCommentCapOverride })
            .FirstOrDefaultAsync();

        if (demoOwner == null)
            return null;

        var cap =
            demoOwner.DemoCommentCapOverride
            ?? await _settings.GetIntAsync(ISettingsService.DemoCommentCap, 10);
        var existing = await _unitOfWork
            .Repository<Comment>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(c => c.OwnerId == owner && c.DeletedAt == null);

        return existing + importCount > cap
            ? $"Demo limit reached: this demo workspace allows at most {cap} comments."
            : null;
    }

    // ---------------------------------------------------------------------------
    // Shared helpers (mirror CommentService)
    // ---------------------------------------------------------------------------

    private async Task<Dictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid> ids)
    {
        var distinct = ids.Where(g => g != Guid.Empty).Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<Guid, string>();

        return await _unitOfWork
            .Repository<User>()
            .Query()
            .AsNoTracking()
            .Where(u => distinct.Contains(u.PublicId))
            .ToDictionaryAsync(u => u.PublicId, u => u.DisplayName);
    }

    private static IEnumerable<Guid> AuthorIds(Comment c)
    {
        yield return c.AuthorId;
        foreach (var r in c.Replies.Where(r => r.DeletedAt == null))
            yield return r.AuthorId;
        if (c.AppliedBy.HasValue)
            yield return c.AppliedBy.Value;
        if (c.EditedBy.HasValue)
            yield return c.EditedBy.Value;
    }
}
