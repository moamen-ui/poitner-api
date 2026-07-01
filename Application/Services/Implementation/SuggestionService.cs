using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Suggestion;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class SuggestionService : ISuggestionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IEmailService _emailService;

    public SuggestionService(IUnitOfWork unitOfWork, ICurrentUser currentUser, IEmailService emailService)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _emailService = emailService;
    }

    public async Task<Result<SuggestionResponse>> SuggestAsync(int projectId, CreateSuggestionRequest request)
    {
        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length == 0)
            return Result<SuggestionResponse>.Failure(MessageKeys.Suggestion.TextRequired);
        var prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length == 0)
            return Result<SuggestionResponse>.Failure(MessageKeys.Suggestion.PromptRequired);

        // Load the target project via the NORMAL query filter — a cross-tenant id is invisible → NotFound.
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (project == null)
            return Result<SuggestionResponse>.NotFound(MessageKeys.Project.NotFound);

        // Guard: only members who CANNOT edit the project may suggest. Admins/owners add directly.
        if (_currentUser.IsAdmin || project.CreatedBy == _currentUser.Id)
            return Result<SuggestionResponse>.Forbidden(MessageKeys.Suggestion.CanEditDirectly);

        var suggestion = new PredefinedActionSuggestion
        {
            OwnerId = project.OwnerId, // = target project's owner; never null-owner in practice
            ProjectId = project.Id,
            Text = text,
            Prompt = prompt,
            Status = SuggestionStatus.Pending
            // SuggestedBy = BaseEntity.CreatedBy — auto-stamped on insert.
        };

        await _unitOfWork.Repository<PredefinedActionSuggestion>().AddAsync(suggestion);
        await _unitOfWork.SaveChangesAsync();

        // Best-effort admin notification — never blocks or fails the suggestion.
        await NotifyAdminsAsync(project);

        var suggesterName = await ResolveNameAsync(suggestion.CreatedBy);
        return Result<SuggestionResponse>.Success(MapToResponse(suggestion, project, suggesterName), MessageKeys.Suggestion.Created);
    }

    public async Task<Result<List<SuggestionResponse>>> ListPendingAsync()
    {
        // Query filter scopes to this tenant (strict-own). Join to the project to exclude
        // suggestions whose project has been soft-deleted and to surface project name/key.
        var pending = await _unitOfWork.Repository<PredefinedActionSuggestion>()
            .Query()
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && s.Status == SuggestionStatus.Pending)
            .ToListAsync();

        if (pending.Count == 0)
            return Result<List<SuggestionResponse>>.Success(new List<SuggestionResponse>());

        var projectIds = pending.Select(s => s.ProjectId).Distinct().ToList();
        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && p.DeletedAt == null)
            .ToDictionaryAsync(p => p.Id, p => p);

        var names = await ResolveNamesAsync(pending.Select(s => s.CreatedBy));

        var responses = pending
            // Exclude suggestions whose project is soft-deleted (absent from the dictionary).
            .Where(s => projects.ContainsKey(s.ProjectId))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => MapToResponse(s, projects[s.ProjectId], names.GetValueOrDefault(s.CreatedBy)))
            .ToList();

        return Result<List<SuggestionResponse>>.Success(responses);
    }

    public async Task<Result<SuggestionResponse>> ApproveAsync(int id)
    {
        var suggestion = await LoadOwnAsync(id);
        if (suggestion == null)
            return Result<SuggestionResponse>.NotFound(MessageKeys.Suggestion.NotFound);

        if (suggestion.Status != SuggestionStatus.Pending)
            return Result<SuggestionResponse>.Conflict(MessageKeys.Suggestion.NotFound);

        // BINDING #4: re-validate the target project still exists + is active before minting an action.
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .Where(p => p.Id == suggestion.ProjectId && p.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (project == null || !project.IsActive)
            return Result<SuggestionResponse>.Conflict(MessageKeys.Suggestion.ProjectUnavailable);

        // SortOrder = max(project-scoped) + 1.
        var existingSorts = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.ProjectId == project.Id)
            .Select(a => (int?)a.SortOrder)
            .ToListAsync();
        var nextSort = (existingSorts.Count == 0 ? -1 : existingSorts.Max() ?? -1) + 1;

        var action = new PredefinedAction
        {
            OwnerId = suggestion.OwnerId,
            ProjectId = suggestion.ProjectId,
            UserId = null,
            Text = suggestion.Text,
            Prompt = suggestion.Prompt,
            IsActive = true,
            SortOrder = nextSort
        };
        await _unitOfWork.Repository<PredefinedAction>().AddAsync(action);

        suggestion.Status = SuggestionStatus.Approved;
        suggestion.ReviewedBy = _currentUser.Id;
        suggestion.ReviewedAt = DateTime.UtcNow;
        _unitOfWork.Repository<PredefinedActionSuggestion>().Update(suggestion);

        await _unitOfWork.SaveChangesAsync();

        var name = await ResolveNameAsync(suggestion.CreatedBy);
        return Result<SuggestionResponse>.Success(MapToResponse(suggestion, project, name), MessageKeys.Suggestion.Approved);
    }

    public async Task<Result<SuggestionResponse>> RejectAsync(int id)
    {
        var suggestion = await LoadOwnAsync(id);
        if (suggestion == null)
            return Result<SuggestionResponse>.NotFound(MessageKeys.Suggestion.NotFound);

        if (suggestion.Status != SuggestionStatus.Pending)
            return Result<SuggestionResponse>.Conflict(MessageKeys.Suggestion.NotFound);

        suggestion.Status = SuggestionStatus.Rejected;
        suggestion.ReviewedBy = _currentUser.Id;
        suggestion.ReviewedAt = DateTime.UtcNow;
        _unitOfWork.Repository<PredefinedActionSuggestion>().Update(suggestion);
        await _unitOfWork.SaveChangesAsync();

        var project = await _unitOfWork.Repository<Project>()
            .Query().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == suggestion.ProjectId);
        var name = await ResolveNameAsync(suggestion.CreatedBy);
        return Result<SuggestionResponse>.Success(MapToResponse(suggestion, project, name), MessageKeys.Suggestion.Rejected);
    }

    // Explicit own-owner load (mirrors PredefinedActionService.LoadOwnTenantWideAsync): scope by the
    // caller's own owner rather than relying on the (strict-own) filter alone. Super-admin sees all.
    private async Task<PredefinedActionSuggestion?> LoadOwnAsync(int id)
    {
        var q = _unitOfWork.Repository<PredefinedActionSuggestion>()
            .Query()
            .IgnoreQueryFilters()
            .Where(s => s.Id == id && s.DeletedAt == null);

        if (!_currentUser.IsSuperAdmin)
        {
            var owner = _currentUser.TenantId;
            q = q.Where(s => s.OwnerId == owner);
        }

        return await q.FirstOrDefaultAsync();
    }

    // Best-effort: resolve the tenant's GrantsAdmin users' emails and notify them. Never throws.
    private async Task NotifyAdminsAsync(Project project)
    {
        try
        {
            var admins = await _unitOfWork.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(u => u.Role)
                .Where(u => u.DeletedAt == null
                            && u.IsActive
                            && u.OwnerId == project.OwnerId
                            && u.Role.GrantsAdmin
                            && !u.Role.IsSuperAdmin)
                .Select(u => new { u.Email, u.RecipientEmail })
                .ToListAsync();

            var subject = "New predefined-prompt suggestion for review";
            var html = $"<p>A stakeholder suggested a predefined prompt for project <b>{project.Name}</b>.</p>" +
                       "<p>Review it in your Pointer dashboard.</p>";

            foreach (var admin in admins)
            {
                var to = string.IsNullOrWhiteSpace(admin.RecipientEmail) ? admin.Email : admin.RecipientEmail;
                if (string.IsNullOrWhiteSpace(to)) continue;
                try { await _emailService.SendAsync(to, subject, html); }
                catch { /* quota/transport failure is non-fatal */ }
            }
        }
        catch { /* notification is best-effort — never block the suggestion */ }
    }

    private async Task<Dictionary<Guid, string>> ResolveNamesAsync(IEnumerable<Guid> ids)
    {
        var distinct = ids.Where(g => g != Guid.Empty).Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<Guid, string>();

        return await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => distinct.Contains(u.PublicId))
            .ToDictionaryAsync(u => u.PublicId, u => u.DisplayName);
    }

    private async Task<string?> ResolveNameAsync(Guid id) =>
        (await ResolveNamesAsync(new[] { id })).GetValueOrDefault(id);

    private static SuggestionResponse MapToResponse(PredefinedActionSuggestion s, Project? project, string? suggestedByName) => new()
    {
        Id = s.Id,
        ProjectId = s.ProjectId,
        ProjectKey = project?.Key ?? string.Empty,
        ProjectName = project?.Name ?? string.Empty,
        Text = s.Text,
        Prompt = s.Prompt,
        Status = s.Status,
        SuggestedByName = suggestedByName,
        CreatedAt = s.CreatedAt
    };
}
