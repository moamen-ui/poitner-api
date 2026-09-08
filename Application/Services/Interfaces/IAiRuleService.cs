using Pointer.Application.DTOs.AiRule;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAiRuleService
{
    Task<Result<List<AiRuleResponse>>> ListTenantAdminRulesAsync();
    Task<Result<List<AiRuleResponse>>> ListProjectAdminRulesAsync(int projectId);
    Task<Result<ProjectAiRulesResponse>> GetProjectRulesAsync(string projectKey);
    Task<Result<List<AiRuleResponse>>> ListMyRulesAsync(int? projectId = null);
    Task<Result<AiRuleResponse>> CreateAsync(CreateAiRuleRequest request);
    Task<Result<AiRuleResponse>> UpdateAsync(int id, UpdateAiRuleRequest request);
    Task<Result> DeleteAsync(int id);
    Task<Result<AiInsightsResponse>> GetInsightsAsync();
    Task<List<AiRuleApplyDto>> GetEffectiveRulesForCommentAsync(int projectId, Guid commentAuthorId);
}
