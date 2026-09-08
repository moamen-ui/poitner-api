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

    public AiRuleService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<List<AiRuleResponse>>> ListTenantAdminRulesAsync()
    {
        if (_currentUser.IsQuickAccess)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var rules = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ProjectId == null && r.UserId == null)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return Result<List<AiRuleResponse>>.Success(rules.Select(r => MapToResponse(r, null, null)).ToList());
    }

    public async Task<Result<List<AiRuleResponse>>> ListProjectAdminRulesAsync(int projectId)
    {
        if (_currentUser.IsQuickAccess)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && p.DeletedAt == null);

        if (project == null)
            return Result<List<AiRuleResponse>>.NotFound(MessageKeys.Project.NotFound);

        var rules = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ProjectId == projectId && r.UserId == null)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return Result<List<AiRuleResponse>>.Success(rules.Select(r => MapToResponse(r, project.Name, null)).ToList());
    }

    public async Task<Result<ProjectAiRulesResponse>> GetProjectRulesAsync(string projectKey)
    {
        if (_currentUser.IsQuickAccess)
            return Result<ProjectAiRulesResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Key == projectKey && p.DeletedAt == null);

        if (project == null)
            return Result<ProjectAiRulesResponse>.NotFound(MessageKeys.Project.NotFound);

        // 1. Admin rules: Tenant-wide (ProjectId == null && UserId == null) + Project-scoped (ProjectId == project.Id && UserId == null)
        var adminRules = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.UserId == null && (r.ProjectId == null || r.ProjectId == project.Id))
            .OrderBy(r => r.ProjectId == null ? 0 : 1)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        // 2. My personal rules for this project/tenant
        var myUserId = _currentUser.Id;
        var myRules = myUserId.HasValue
            ? await _unitOfWork.Repository<AiRule>()
                .Query()
                .AsNoTracking()
                .Where(r => r.DeletedAt == null && r.UserId == myUserId.Value && (r.ProjectId == null || r.ProjectId == project.Id))
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.CreatedAt)
                .ToListAsync()
            : new List<AiRule>();

        return Result<ProjectAiRulesResponse>.Success(new ProjectAiRulesResponse
        {
            ProjectId = project.Id,
            ProjectKey = project.Key,
            ProjectName = project.Name,
            AdminRules = adminRules.Select(r => MapToResponse(r, r.ProjectId.HasValue ? project.Name : null, null)).ToList(),
            MyRules = myRules.Select(r => MapToResponse(r, r.ProjectId.HasValue ? project.Name : null, null)).ToList()
        });
    }

    public async Task<Result<List<AiRuleResponse>>> ListMyRulesAsync(int? projectId = null)
    {
        if (_currentUser.IsQuickAccess)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var myUserId = _currentUser.Id;
        if (!myUserId.HasValue)
            return Result<List<AiRuleResponse>>.Forbidden(MessageKeys.Common.Forbidden);

        var query = _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.UserId == myUserId.Value);

        if (projectId.HasValue)
            query = query.Where(r => r.ProjectId == null || r.ProjectId == projectId.Value);

        var rules = await query
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return Result<List<AiRuleResponse>>.Success(rules.Select(r => MapToResponse(r, null, null)).ToList());
    }

    public async Task<Result<AiRuleResponse>> CreateAsync(CreateAiRuleRequest request)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

        if (_currentUser.IsQuickAccess)
            return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner)
            return Result<AiRuleResponse>.Forbidden(MessageKeys.Common.Forbidden);

        string? projectName = null;
        if (request.ProjectId.HasValue)
        {
            var project = await _unitOfWork.Repository<Project>()
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

        var sortOrder = request.SortOrder ?? await NextSortOrderAsync(request.ProjectId, targetUserId);

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
            CreatedBy = _currentUser.Id ?? Guid.Empty
        };

        await _unitOfWork.Repository<AiRule>().AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        return Result<AiRuleResponse>.Success(MapToResponse(entity, projectName, null));
    }

    public async Task<Result<AiRuleResponse>> UpdateAsync(int id, UpdateAiRuleRequest request)
    {
        var rule = await _unitOfWork.Repository<AiRule>()
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

        return Result<AiRuleResponse>.Success(MapToResponse(rule, null, null));
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var rule = await _unitOfWork.Repository<AiRule>()
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

        return Result.Success();
    }

    public async Task<Result<AiInsightsResponse>> GetInsightsAsync()
    {
        if (!_currentUser.IsAdmin && !_currentUser.IsSuperAdmin)
            return Result<AiInsightsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var rulesQuery = _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null);

        var allRules = await rulesQuery
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var totalCount = allRules.Count;
        var tenantCount = allRules.Count(r => r.ProjectId == null && r.UserId == null);
        var projectCount = allRules.Count(r => r.ProjectId != null && r.UserId == null);
        var userPersonalCount = allRules.Count(r => r.UserId != null);

        // Tool usage from projects
        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.AiToolsUsed != null)
            .Select(p => p.AiToolsUsed)
            .ToListAsync();

        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in projects)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var tools = JsonSerializer.Deserialize<List<string>>(raw);
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

        // Summaries of user personal rules
        var userIds = allRules.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var users = userIds.Count > 0
            ? await _unitOfWork.Repository<User>()
                .Query()
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(u => userIds.Contains(u.PublicId))
                .ToDictionaryAsync(u => u.PublicId, u => u.DisplayName)
            : new Dictionary<Guid, string>();

        var userSummaries = allRules
            .Where(r => r.UserId != null)
            .GroupBy(r => r.UserId!.Value)
            .Select(g => new UserRuleSummaryStat
            {
                UserId = g.Key,
                UserName = users.GetValueOrDefault(g.Key) ?? "Unknown",
                RulesCount = g.Count()
            })
            .OrderByDescending(s => s.RulesCount)
            .ToList();

        var recent = allRules.Take(10).Select(r => MapToResponse(r, null, r.UserId.HasValue ? users.GetValueOrDefault(r.UserId.Value) : null)).ToList();

        return Result<AiInsightsResponse>.Success(new AiInsightsResponse
        {
            TotalRulesCount = totalCount,
            TenantRulesCount = tenantCount,
            ProjectRulesCount = projectCount,
            UserPersonalRulesCount = userPersonalCount,
            ToolUsage = toolCounts.Select(kv => new AiToolUsageStat { ToolName = kv.Key, ProjectCount = kv.Value }).ToList(),
            UserRuleSummaries = userSummaries,
            RecentRules = recent
        });
    }

    public async Task<List<AiRuleApplyDto>> GetEffectiveRulesForCommentAsync(int projectId, Guid commentAuthorId)
    {
        var rules = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.IsActive &&
                ((r.UserId == null && (r.ProjectId == null || r.ProjectId == projectId)) ||
                 (r.UserId == commentAuthorId && (r.ProjectId == null || r.ProjectId == projectId))))
            .OrderBy(r => r.UserId == null ? 0 : 1) // Admin rules first, then personal rules
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync();

        return rules.Select(r => new AiRuleApplyDto
        {
            Title = r.Title,
            Prompt = r.Prompt,
            IsPersonal = r.UserId != null
        }).ToList();
    }

    private async Task<int> NextSortOrderAsync(int? projectId, Guid? userId)
    {
        var last = await _unitOfWork.Repository<AiRule>()
            .Query()
            .AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ProjectId == projectId && r.UserId == userId)
            .OrderByDescending(r => r.SortOrder)
            .Select(r => r.SortOrder)
            .FirstOrDefaultAsync();

        return last + 1;
    }

    private AiRuleResponse MapToResponse(AiRule r, string? projectName, string? userName)
    {
        var canEdit = _currentUser.IsAdmin || (_currentUser.Id.HasValue && r.UserId == _currentUser.Id.Value);
        var canDelete = canEdit;

        return new AiRuleResponse
        {
            Id = r.Id,
            ProjectId = r.ProjectId,
            ProjectName = projectName,
            UserId = r.UserId,
            UserName = userName,
            Title = r.Title,
            Prompt = r.Prompt,
            IsActive = r.IsActive,
            SortOrder = r.SortOrder,
            CreatedAt = r.CreatedAt,
            CanEdit = canEdit,
            CanDelete = canDelete
        };
    }
}
