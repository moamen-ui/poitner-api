namespace Pointer.Application.DTOs.AiRule;

public class AiInsightsResponse
{
    public int TotalRulesCount { get; set; }
    public int TenantRulesCount { get; set; }
    public int ProjectRulesCount { get; set; }
    public int UserPersonalRulesCount { get; set; }
    public List<AiToolUsageStat> ToolUsage { get; set; } = new();
    public List<UserRuleSummaryStat> UserRuleSummaries { get; set; } = new();
    public List<TenantRuleSummaryStat>? TenantSummaries { get; set; }
    public List<AiRuleResponse> RecentRules { get; set; } = new();
    public List<AiRuleResponse>? DetailedRules { get; set; }
}

public class AiToolUsageStat
{
    public string ToolName { get; set; } = string.Empty;
    public int ProjectCount { get; set; }
}

public class UserRuleSummaryStat
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? UserEmail { get; set; }
    public int RulesCount { get; set; }
}

public class TenantRuleSummaryStat
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int RulesCount { get; set; }
    public int ProjectsCount { get; set; }
}
