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

    // --- export limits (H5: bound export memory; symmetric with the import cap) ---
    private const int MaxExportCommentCount = 5000;
    private const int ExportBatchSize = 500;

    // --- import flush cadence (M10: bound the change-tracker / transaction size) ---
    private const int ImportSaveBatchSize = 200;

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

        return await BuildExportFileAsync(options, projectId: projectResult.Data, sourceProject: projectKey);
    }

    public async Task<Result<ExportFileDto>> ExportWorkspaceAsync(ExportOptions options)
    {
        return await BuildExportFileAsync(options, projectId: null, sourceProject: null);
    }

    /// <summary>
    /// Builds the filtered comment query (tenant isolation via the EF global query filter on Comment,
    /// plus the explicit ProjectId / private / deleted clamps). Does NOT materialize or Include replies —
    /// callers add <c>.Include(c =&gt; c.Replies)</c> per keyset batch (H5).
    /// </summary>
    private IQueryable<Comment> FilteredCommentQuery(ExportOptions options, int? projectId)
    {
        // IncludePrivate / IncludeDeleted require admin — clamped server-side regardless of input.
        var includePrivate = options.IncludePrivate && _currentUser.IsAdmin;
        var includeDeleted = options.IncludeDeleted && _currentUser.IsAdmin;
        var callerId = _currentUser.Id ?? Guid.Empty;

        IQueryable<Comment> query = _unitOfWork
            .Repository<Comment>()
            .Query()
            .AsNoTracking();

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

        return query;
    }

    private async Task<Result<ExportFileDto>> BuildExportFileAsync(
        ExportOptions options,
        int? projectId,
        string? sourceProject
    )
    {
        var baseQuery = FilteredCommentQuery(options, projectId);

        // H5: refuse before materializing anything if the match set exceeds the cap.
        var totalCount = await baseQuery.CountAsync();
        if (totalCount > MaxExportCommentCount)
            return Result<ExportFileDto>.Failure(
                $"Too many comments to export ({totalCount}). The maximum is {MaxExportCommentCount}; narrow the export with filters (status, environment, or a single project)."
            );

        // Keyset-paginate by Id so at most one batch of entities (with their large `element` blobs)
        // is materialized at a time — the entire tenant comment graph is never held in memory at once.
        var commentDtos = new List<CommentExportDto>(totalCount);
        var names = new Dictionary<Guid, string>();
        var projectKeys = new Dictionary<int, string>();
        var lastId = 0;

        while (true)
        {
            var batch = await baseQuery
                // Replies load under the same tenant filter (global query filter applies).
                // NOTE: AsSplitQuery() would avoid fan-duplicating the parent `element` blob across
                // reply rows, but it is a relational-only extension not referenced by the Application
                // project; keyset batching already bounds memory, so we omit it here.
                .Include(c => c.Replies)
                .Where(c => c.Id > lastId)
                .OrderBy(c => c.Id)
                .Take(ExportBatchSize)
                .ToListAsync();

            if (batch.Count == 0)
                break;

            lastId = batch[^1].Id;

            // Resolve only the author names / project keys this batch introduces (deduped into
            // running maps so repeat authors/projects across batches aren't re-queried).
            await MergeNamesAsync(names, batch.SelectMany(AuthorIds));
            if (sourceProject == null)
                await MergeProjectKeysAsync(projectKeys, batch.Select(c => c.ProjectId));

            foreach (var c in batch)
            {
                var liveReplies = c.Replies.Where(r => r.DeletedAt == null).ToList();
                commentDtos.Add(
                    new CommentExportDto
                    {
                        // ExportId (c-N / r-N) is assigned after the final CreatedAt sort below, so the
                        // numbering matches output order exactly — byte-for-byte with the pre-batch impl.
                        ProjectKey = sourceProject ?? projectKeys.GetValueOrDefault(c.ProjectId),
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
                                Body = r.Body,
                                AuthorDisplayName = names.GetValueOrDefault(r.AuthorId),
                                CreatedAt = r.CreatedAt
                            })
                            .ToList()
                    }
                );
            }
        }

        // Restore the CreatedAt-ascending ordering guarantee (stable LINQ OrderBy; capped at 5000),
        // then assign the sequential export ids in output order.
        var ordered = commentDtos.OrderBy(c => c.CreatedAt).ToList();
        var replySeq = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].ExportId = $"c-{i + 1}";
            foreach (var r in ordered[i].Replies)
                r.ExportId = $"r-{++replySeq}";
        }

        return Result<ExportFileDto>.Success(
            new ExportFileDto
            {
                SchemaVersion = CurrentSchemaVersion,
                ExportedAt = DateTime.UtcNow,
                SourceProject = sourceProject,
                SourceServer = null, // informational; left null (controller sets Content-Disposition)
                Comments = ordered
            },
            MessageKeys.ExportImport.Exported
        );
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
        var comments = 0;
        var replies = 0;
        // Atomic import: the batched SaveChanges (M10) run INSIDE one transaction, so a mid-import
        // failure rolls the whole thing back (restores the pre-batching all-or-nothing guarantee)
        // while ClearChangeTracker between batches still bounds change-tracker memory.
        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            (comments, replies) = await InsertCommentsAsync(file.Comments, projectId, projectOwnerId, warnings);
            await _unitOfWork.SaveChangesAsync();
        });
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

        // Phase 1 (READ-ONLY): validate + resolve every group up front, so a bad group returns early
        // WITHOUT any writes. Cap checks read the pre-import DB count (same as before batching).
        var plan = new List<(List<CommentExportDto> Group, int ProjectId, Guid OwnerId)>();
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

            plan.Add((groupList, projectId, projectOwnerId));
        }

        // Phase 2 (ATOMIC): insert every group inside one transaction — all-or-nothing on failure,
        // with batched SaveChanges/ClearChangeTracker (M10) bounding memory within the transaction.
        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            foreach (var (group, projectId, projectOwnerId) in plan)
            {
                var (c, r) = await InsertCommentsAsync(group, projectId, projectOwnerId, warnings);
                totalComments += c;
                totalReplies += r;
            }
            await _unitOfWork.SaveChangesAsync();
        });
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

            // M10: flush roughly every ImportSaveBatchSize comments so a single import (up to
            // 5000 comments × 500 replies) is not accumulated into one giant change-tracker /
            // transaction — this bounds the lock window and the per-save memory spike. After each
            // flush, detach the saved graph via ClearChangeTracker() so the tracker doesn't grow.
            // PreserveCreatedAtOnInsert semantics are preserved: it is applied per entity before
            // AddAsync, and AppDbContext.SaveChangesAsync consumes the flag on the Added entity at
            // each save (already-saved rows are Unchanged and never re-stamped); clearing afterward
            // is safe because we never reference saved entities again.
            if (commentCount % ImportSaveBatchSize == 0)
            {
                await _unitOfWork.SaveChangesAsync();
                _unitOfWork.ClearChangeTracker();
            }
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

    /// <summary>
    /// Resolves display names for any author ids not already in <paramref name="names"/> and merges
    /// them in. Called per export batch so only the newly-seen authors are queried.
    /// </summary>
    private async Task MergeNamesAsync(Dictionary<Guid, string> names, IEnumerable<Guid> ids)
    {
        var missing = ids.Where(g => g != Guid.Empty && !names.ContainsKey(g)).Distinct().ToList();
        if (missing.Count == 0)
            return;

        var resolved = await _unitOfWork
            .Repository<User>()
            .Query()
            .AsNoTracking()
            .Where(u => missing.Contains(u.PublicId))
            .ToDictionaryAsync(u => u.PublicId, u => u.DisplayName);

        foreach (var kv in resolved)
            names[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Resolves project keys for any project ids not already in <paramref name="keys"/> and merges
    /// them in (workspace export only). Called per export batch so only newly-seen projects are queried.
    /// </summary>
    private async Task MergeProjectKeysAsync(Dictionary<int, string> keys, IEnumerable<int> projectIds)
    {
        var missing = projectIds.Where(id => !keys.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
            return;

        var resolved = await _unitOfWork
            .Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => missing.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Key);

        foreach (var kv in resolved)
            keys[kv.Key] = kv.Value;
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
