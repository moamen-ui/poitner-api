using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.AiRule;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class AiRuleService : IAiRuleService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditWriter _audit;

    public AiRuleService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IAuditWriter? audit = null
    )
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    public async Task<Result<List<AiRuleResponse>>> ListTenantAdminRulesAsync()
    {
        if (_currentUser.IsQuickAccess)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var rules = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ProjectId == null && r.UserId == null)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return Result<List<AiRuleResponse>>.Success(
            rules.Select(r => MapToResponse(r, null, null)).ToList()
        );
    }

    public async Task<Result<List<AiRuleResponse>>> ListProjectAdminRulesAsync(int projectId)
    {
        if (_currentUser.IsQuickAccess)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var project = await _unitOfWork
            .Repository<Project>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null);

        if (project == null)
            return Result<List<AiRuleResponse>>.NotFound(MessageKeys.Project.NotFound);

        var rules = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ProjectId == projectId && r.UserId == null)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return Result<List<AiRuleResponse>>.Success(
            rules.Select(r => MapToResponse(r, project.Name, null)).ToList()
        );
    }

    public async Task<Result<ProjectAiRulesResponse>> GetProjectRulesAsync(string projectKey)
    {
        if (_currentUser.IsQuickAccess)
            return Result<ProjectAiRulesResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var project = await _unitOfWork
            .Repository<Project>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == projectKey && p.DeletedAt == null);

        if (project == null)
            return Result<ProjectAiRulesResponse>.NotFound(MessageKeys.Project.NotFound);

        // 1. Admin rules: Tenant-wide (ProjectId == null && UserId == null) + Project-scoped (ProjectId == project.Id && UserId == null)
        var adminRules = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r =>
                r.DeletedAt == null
                && r.UserId == null
                && (r.ProjectId == null || r.ProjectId == project.Id)
            )
            .OrderBy(r => r.ProjectId == null ? 0 : 1)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        // 2. My personal rules for this project/tenant
        var myUserId = _currentUser.Id;
        var myRules = myUserId.HasValue
            ? await _unitOfWork
                .Repository<AiRule>()
                .Query()
                .AsNoTracking()
                .Where(r =>
                    r.DeletedAt == null
                    && r.UserId == myUserId.Value
                    && (r.ProjectId == null || r.ProjectId == project.Id)
                )
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.CreatedAt)
                .ToListAsync()
            : new List<AiRule>();

        return Result<ProjectAiRulesResponse>.Success(
            new ProjectAiRulesResponse
            {
                ProjectId = project.Id,
                ProjectKey = project.Key,
                ProjectName = project.Name,
                AdminRules = adminRules
                    .Select(r => MapToResponse(r, r.ProjectId.HasValue ? project.Name : null, null))
                    .ToList(),
                MyRules = myRules
                    .Select(r => MapToResponse(r, r.ProjectId.HasValue ? project.Name : null, null))
                    .ToList(),
            }
        );
    }

    public async Task<Result<List<AiRuleResponse>>> ListMyRulesAsync(int? projectId = null)
    {
        if (_currentUser.IsQuickAccess)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var myUserId = _currentUser.Id;
        if (!myUserId.HasValue)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var query = _unitOfWork
            .Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.UserId == myUserId.Value);

        if (projectId.HasValue)
            query = query.Where(r => r.ProjectId == null || r.ProjectId == projectId.Value);

        var rules = await query.OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt).ToListAsync();

        return Result<List<AiRuleResponse>>.Success(
            rules.Select(r => MapToResponse(r, null, null)).ToList()
        );
    }

    public async Task<Result<AiRuleResponse>> CreateAsync(CreateAiRuleRequest request)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

        if (_currentUser.IsQuickAccess)
            return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var ownerId = TenantStamp.OwnerFor(_currentUser);
        if (ownerId is not Guid owner)
            return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

        string? projectName = null;
        if (request.ProjectId.HasValue)
        {
            var project = await _unitOfWork
                .Repository<Project>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ProjectId.Value && p.DeletedAt == null);

            if (project == null)
                return Result<AiRuleResponse>.NotFound(MessageKeys.Project.NotFound);

            projectName = project.Name;
        }

        Guid? targetUserId = null;

        if (request.IsPersonal)
        {
            // Non-admin creating a personal rule for their own comments
            var currentUserId = _currentUser.Id;
            if (!currentUserId.HasValue)
                return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

            targetUserId = currentUserId.Value;
        }
        else
        {
            // Admin or deputy creating a tenant- or project-level rule
            if (!_currentUser.IsAdmin)
                return Result<AiRuleResponse>.Forbidden(MessageKeys.AiRule.Forbidden);

            targetUserId = null;
        }

        var sortOrder =
            request.SortOrder ?? await NextSortOrderAsync(request.ProjectId, targetUserId);

        var entity = new AiRule
        {
            OwnerId = owner,
            ProjectId = request.ProjectId,
            UserId = targetUserId,
            Title = request.Title.Trim(),
            Prompt = request.Prompt.Trim(),
            IsActive = true,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = _currentUser.Id ?? Guid.Empty,
        };

        await _unitOfWork.Repository<AiRule>().AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AiRuleCreated,
                AuditTargets.AiRule,
                entity.Id.ToString(),
                owner,
                After: entity.ProjectId is int pid
                    ? new Dictionary<string, string> { ["project_id"] = pid.ToString() }
                    : null
            )
        );

        return Result<AiRuleResponse>.Success(MapToResponse(entity, projectName, null));
    }

    public async Task<Result<AiRuleResponse>> UpdateAsync(int id, UpdateAiRuleRequest request)
    {
        var rule = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null);

        if (rule == null)
            return Result<AiRuleResponse>.NotFound(MessageKeys.AiRule.NotFound);

        // Permissions:
        // Admin rules (UserId == null) require admin.
        // Personal rules (UserId != null) require owner or admin.
        if (rule.UserId == null)
        {
            if (!_currentUser.IsAdmin)
                return Result<AiRuleResponse>.Forbidden(MessageKeys.AiRule.Forbidden);
        }
        else
        {
            if (rule.UserId != _currentUser.Id && !_currentUser.IsAdmin)
                return Result<AiRuleResponse>.Forbidden(MessageKeys.AiRule.Forbidden);
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
            rule.Title = request.Title.Trim();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            rule.Prompt = request.Prompt.Trim();

        if (request.IsActive.HasValue)
            rule.IsActive = request.IsActive.Value;

        if (request.SortOrder.HasValue)
            rule.SortOrder = request.SortOrder.Value;

        rule.UpdatedAt = DateTime.UtcNow;
        rule.UpdatedBy = _currentUser.Id;

        _unitOfWork.Repository<AiRule>().Update(rule);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AiRuleUpdated,
                AuditTargets.AiRule,
                rule.Id.ToString(),
                rule.OwnerId,
                After: rule.ProjectId is int pid
                    ? new Dictionary<string, string> { ["project_id"] = pid.ToString() }
                    : null
            )
        );

        return Result<AiRuleResponse>.Success(MapToResponse(rule, null, null));
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var rule = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null);

        if (rule == null)
            return Result.NotFound(MessageKeys.AiRule.NotFound);

        if (rule.UserId == null)
        {
            if (!_currentUser.IsAdmin)
                return Result.Forbidden(MessageKeys.AiRule.Forbidden);
        }
        else
        {
            if (rule.UserId != _currentUser.Id && !_currentUser.IsAdmin)
                return Result.Forbidden(MessageKeys.AiRule.Forbidden);
        }

        rule.DeletedAt = DateTime.UtcNow;
        rule.DeletedBy = _currentUser.Id;

        _unitOfWork.Repository<AiRule>().Update(rule);
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AiRuleDeleted,
                AuditTargets.AiRule,
                rule.Id.ToString(),
                rule.OwnerId,
                After: rule.ProjectId is int pid
                    ? new Dictionary<string, string> { ["project_id"] = pid.ToString() }
                    : null
            )
        );

        return Result.Success();
    }

    public async Task<Result<AiInsightsResponse>> GetInsightsAsync(
        Guid? tenantId = null,
        bool includeDetails = false
    )
    {
        if (!_currentUser.IsAdmin && !_currentUser.IsSuperAdmin)
            return Result<AiInsightsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        if (
            !_currentUser.IsSuperAdmin
            && tenantId.HasValue
            && tenantId.Value != _currentUser.TenantId
        )
            return Result<AiInsightsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // DB-13 (F2): an impersonating operator is pinned to the session's workspace, not the
        // arbitrary `tenantId` query param.
        var effectiveTenantId =
            _currentUser.IsSuperAdmin && !_currentUser.IsImpersonating
                ? tenantId
                : _currentUser.TenantId;

        var rulesQuery = _unitOfWork
            .Repository<AiRule>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null);

        if (effectiveTenantId.HasValue)
        {
            rulesQuery = rulesQuery.Where(r => r.OwnerId == effectiveTenantId.Value);
        }

        var allRules = await rulesQuery.OrderByDescending(r => r.CreatedAt).ToListAsync();

        var totalCount = allRules.Count;
        var tenantCount = allRules.Count(r => r.ProjectId == null && r.UserId == null);
        var projectCount = allRules.Count(r => r.ProjectId != null && r.UserId == null);
        var userPersonalCount = allRules.Count(r => r.UserId != null);

        // Tool usage from projects
        var projectsQuery = _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.AiToolsUsed != null);

        if (effectiveTenantId.HasValue)
        {
            projectsQuery = projectsQuery.Where(p => p.OwnerId == effectiveTenantId.Value);
        }

        var projects = await projectsQuery
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.OwnerId,
                p.AiToolsUsed,
            })
            .ToListAsync();

        var projectMap = projects.ToDictionary(p => p.Id, p => p.Name);

        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            if (string.IsNullOrWhiteSpace(p.AiToolsUsed))
                continue;
            try
            {
                var tools = JsonSerializer.Deserialize<List<string>>(p.AiToolsUsed);
                if (tools != null)
                {
                    foreach (var t in tools)
                    {
                        var key = t.Trim();
                        toolCounts[key] = toolCounts.GetValueOrDefault(key) + 1;
                    }
                }
            }
            catch { }
        }

        // Users lookup
        var userIds = allRules
            .Where(r => r.UserId != null)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .ToList();
        var users = await UserNameResolver.ResolveWithEmailAsync(
            _unitOfWork,
            userIds,
            ignoreQueryFilters: true
        );

        var userSummaries = allRules
            .Where(r => r.UserId != null)
            .GroupBy(r => r.UserId!.Value)
            .Select(g => new UserRuleSummaryStat
            {
                UserId = g.Key,
                UserName = users.TryGetValue(g.Key, out var u) ? u.DisplayName : "Unknown",
                UserEmail = users.TryGetValue(g.Key, out var u2) ? u2.Email : null,
                RulesCount = g.Count(),
            })
            .OrderByDescending(s => s.RulesCount)
            .ToList();

        // Tenants lookup (for super admin platform overview)
        var tenantMap = new Dictionary<Guid, string>();
        List<TenantRuleSummaryStat>? tenantSummaries = null;

        if (_currentUser.IsSuperAdmin)
        {
            var ownerIds = allRules.Select(r => (Guid?)r.OwnerId).Distinct().ToList();
            if (ownerIds.Count > 0)
            {
                tenantMap = await _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(w => ownerIds.Contains(w.Id))
                    .ToDictionaryAsync(w => w.Id, w => w.Name);
            }

            tenantSummaries = allRules
                .Where(r => r.OwnerId.HasValue)
                .GroupBy(r => r.OwnerId!.Value)
                .Select(g => new TenantRuleSummaryStat
                {
                    TenantId = g.Key,
                    TenantName = tenantMap.TryGetValue(g.Key, out var name)
                        ? name
                        : "Workspace " + g.Key.ToString()[..8],
                    RulesCount = g.Count(),
                    ProjectsCount = g.Where(r => r.ProjectId != null)
                        .Select(r => r.ProjectId!.Value)
                        .Distinct()
                        .Count(),
                })
                .OrderByDescending(t => t.RulesCount)
                .ToList();
        }

        AiRuleResponse MapRule(AiRule r) =>
            MapToResponse(
                r,
                r.ProjectId.HasValue ? projectMap.GetValueOrDefault(r.ProjectId.Value) : null,
                r.UserId.HasValue && users.TryGetValue(r.UserId.Value, out var u)
                    ? u.DisplayName
                    : null,
                r.UserId.HasValue && users.TryGetValue(r.UserId.Value, out var u2)
                    ? u2.Email
                    : null,
                r.OwnerId.HasValue ? tenantMap.GetValueOrDefault(r.OwnerId.Value) : null
            );

        // DB-13 (F2): rule title/prompt is content — a plain operator gets counts/tool-usage/tenant
        // summaries (metadata) but no rule text; an impersonating operator (pinned above) still does.
        var canReadRules = !_currentUser.IsSuperAdmin || _currentUser.IsImpersonating;
        var recent = canReadRules
            ? allRules.Take(10).Select(MapRule).ToList()
            : new List<AiRuleResponse>();
        var detailed =
            canReadRules && (includeDetails || _currentUser.IsSuperAdmin)
                ? allRules.Select(MapRule).ToList()
                : null;

        return Result<AiInsightsResponse>.Success(
            new AiInsightsResponse
            {
                TotalRulesCount = totalCount,
                TenantRulesCount = tenantCount,
                ProjectRulesCount = projectCount,
                UserPersonalRulesCount = userPersonalCount,
                ToolUsage = toolCounts
                    .Select(kv => new AiToolUsageStat
                    {
                        ToolName = kv.Key,
                        ProjectCount = kv.Value,
                    })
                    .ToList(),
                UserRuleSummaries = userSummaries,
                TenantSummaries = tenantSummaries,
                RecentRules = recent,
                DetailedRules = detailed,
            }
        );
    }

    public async Task<Result<List<AiRuleResponse>>> ListAllRulesAsync(
        Guid? tenantId = null,
        int? projectId = null
    )
    {
        if (!_currentUser.IsAdmin && !_currentUser.IsSuperAdmin)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        // DB-13 (F2): rule title/prompt is content — a plain operator listing "all rules" across
        // every workspace is refused; an impersonating operator is pinned to the session's workspace.
        if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Impersonation.Required);

        // DB-13 review fix #5 (LOW): a non-super-admin caller with no tenant claim at all must never
        // reach the query below — effectiveTenantId would be null, tenantId.HasValue would be false,
        // and the unconditional IgnoreQueryFilters() a few lines down would then return every
        // workspace's rule text to them.
        if (!_currentUser.IsSuperAdmin && _currentUser.TenantId is null)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        if (
            !_currentUser.IsSuperAdmin
            && tenantId.HasValue
            && tenantId.Value != _currentUser.TenantId
        )
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var effectiveTenantId = _currentUser.TenantId;

        var query = _unitOfWork
            .Repository<AiRule>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null);

        if (effectiveTenantId.HasValue)
            query = query.Where(r => r.OwnerId == effectiveTenantId.Value);

        if (projectId.HasValue)
            query = query.Where(r => r.ProjectId == projectId.Value);

        var rules = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();

        var projectIds = rules
            .Where(r => r.ProjectId != null)
            .Select(r => r.ProjectId!.Value)
            .Distinct()
            .ToList();
        var projectMap =
            projectIds.Count > 0
                ? await _unitOfWork
                    .Repository<Project>()
                    .Query()
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(p => projectIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p.Name)
                : new Dictionary<int, string>();

        var userIds = rules
            .Where(r => r.UserId != null)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .ToList();
        var users = await UserNameResolver.ResolveWithEmailAsync(
            _unitOfWork,
            userIds,
            ignoreQueryFilters: true
        );

        var tenantMap = new Dictionary<Guid, string>();
        if (_currentUser.IsSuperAdmin)
        {
            var ownerIds = rules.Select(r => (Guid?)r.OwnerId).Distinct().ToList();
            if (ownerIds.Count > 0)
            {
                tenantMap = await _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(w => ownerIds.Contains(w.Id))
                    .ToDictionaryAsync(w => w.Id, w => w.Name);
            }
        }

        var result = rules
            .Select(r =>
                MapToResponse(
                    r,
                    r.ProjectId.HasValue ? projectMap.GetValueOrDefault(r.ProjectId.Value) : null,
                    r.UserId.HasValue && users.TryGetValue(r.UserId.Value, out var u)
                        ? u.DisplayName
                        : null,
                    r.UserId.HasValue && users.TryGetValue(r.UserId.Value, out var u2)
                        ? u2.Email
                        : null,
                    r.OwnerId.HasValue ? tenantMap.GetValueOrDefault(r.OwnerId.Value) : null
                )
            )
            .ToList();

        return Result<List<AiRuleResponse>>.Success(result);
    }

    public async Task<List<AiRuleApplyDto>> GetEffectiveRulesForCommentAsync(
        int projectId,
        Guid commentAuthorId
    )
    {
        var rules = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r =>
                r.DeletedAt == null
                && r.IsActive
                && (
                    (r.UserId == null && (r.ProjectId == null || r.ProjectId == projectId))
                    || (
                        r.UserId == commentAuthorId
                        && (r.ProjectId == null || r.ProjectId == projectId)
                    )
                )
            )
            .OrderBy(r => r.UserId == null ? (r.ProjectId == null ? 0 : 1) : 2) // Strict priority: Workspace (0) > Project (1) > Personal (2)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return rules
            .Select(r => new AiRuleApplyDto
            {
                Title = r.Title,
                Prompt = r.Prompt,
                Scope =
                    r.UserId != null ? "Personal" : (r.ProjectId == null ? "Workspace" : "Project"),
                Priority = r.UserId != null ? 3 : (r.ProjectId == null ? 1 : 2),
                IsPersonal = r.UserId != null,
            })
            .ToList();
    }

    private async Task<int> NextSortOrderAsync(int? projectId, Guid? userId)
    {
        var last = await _unitOfWork
            .Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ProjectId == projectId && r.UserId == userId)
            .OrderByDescending(r => r.SortOrder)
            .Select(r => r.SortOrder)
            .FirstOrDefaultAsync();

        return last + 1;
    }

    private AiRuleResponse MapToResponse(
        AiRule r,
        string? projectName,
        string? userName,
        string? userEmail = null,
        string? tenantName = null
    )
    {
        var canEdit =
            _currentUser.IsAdmin
            || _currentUser.IsSuperAdmin
            || (_currentUser.Id.HasValue && r.UserId == _currentUser.Id.Value);
        var canDelete = canEdit;

        return new AiRuleResponse
        {
            Id = r.Id,
            TenantId = r.OwnerId,
            TenantName = tenantName,
            ProjectId = r.ProjectId,
            ProjectName = projectName,
            UserId = r.UserId,
            UserName = userName,
            UserEmail = userEmail,
            Title = r.Title,
            Prompt = r.Prompt,
            IsActive = r.IsActive,
            SortOrder = r.SortOrder,
            CreatedAt = r.CreatedAt,
            CanEdit = canEdit,
            CanDelete = canDelete,
        };
    }
}
