namespace Pointer.Application.DTOs.AiRule;

public class AiInsightsResponse
{
    public int TotalRulesCount { get; set; }
    public int TenantRulesCount { get; set; }
    public int ProjectRulesCount { get; set; }
    public int UserPersonalRulesCount { get; set; }
    public List<AiToolUsageStat> ToolUsage { get; set; } = new();
    public List<UserRuleSummaryStat> UserRuleSummaries { get; set; } = new();
    public List<AiRuleResponse> RecentRules { get; set; } = new();
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
    public int RulesCount { get; set; }
}
