using System.Text.RegularExpressions;

public class MigrationSafetyTests
{
    private static readonly string MigrationsDir = Path.Combine(
        RepoRoot.Find(),
        "Infrastructure",
        "Migrations"
    );

    // 58 migration ids, frozen as of 2026-09-22 (DB-02). Never rename or delete one of these files
    // — the `__EFMigrationsHistory` table keys on this exact string. See DB-RULES.md R10.
    internal static readonly HashSet<string> Baseline = new(StringComparer.Ordinal)
    {
        "20260623133436_InitialCreate",
        "20260624130359_AddElementScreenshotUrl",
        "20260624132711_AddUserPreferences",
        "20260624144403_AddUserApprovalStatus",
        "20260624151316_AddCommentIsPrivate",
        "20260624153650_AddCommentEditTrace",
        "20260629072310_AddStatusPresentations",
        "20260629130828_AddTenancy",
        "20260629131255_AddTenantRoleNameIndex",
        "20260629155415_AddDemoColumns",
        "20260701121004_AddDemoTenantConfig",
        "20260701141656_AddUserRecipientEmail",
        "20260701142439_AddPredefinedActions",
        "20260701163243_MultiSelectPickedActions",
        "20260701171558_PredefinedActionNullableOwner",
        "20260701190410_AddInvites",
        "20260701222115_AddCommentBodyMaxLength",
        "20260701225454_AddPredefinedActionSuggestions",
        "20260702014430_AddPlansAndSubscriptions",
        "20260705041708_AddCommentPerfIndexes",
        "20260705173225_AddUserSecurityStamp",
        "20260825100006_AddPageContextCapture",
        "20260825122734_AddUserCommentShortcut",
        "20260827124245_ReassignPointerLandingOwnership",
        "20260827125323_ReassignRemainingSuperAdminOwnedResources",
        "20260827130246_EnforceOwnerIdNotNull",
        "20260827164229_MakeInviteOwnerIdNullable",
        "20260829150137_AddQuickAccessInviteFields",
        "20260830131134_AddRoleTenantOverrides",
        "20260830174750_AddAppEnvironmentsAndProjectAppUrls",
        "20260831153128_MakeProjectKeyIndexFilterSoftDeleted",
        "20260831174453_AddProjectTechStack",
        "20260831183037_AddUserApiKey",
        "20260903191419_ProjectPerEnvironmentActivation",
        "20260903195932_ProjectAppUrlIsActive",
        "20260906094008_AddProjectEnvironmentSelectorRoleIds",
        "20260908132329_AddAiRulesTable",
        "20260909093139_AddCommentCommitUrlAndProjectCommitStyle",
        "20260911170058_AddApiKeysTable",
        "20260911170808_AddAppEnvironmentEnabledFlags",
        "20260911170828_MigrateDefaultProjectAppUrlsToLocal",
        "20260911170928_AddUsageEvents",
        "20260911173001_AddProjectEnforceAllowedOrigins",
        "20260911223426_AddTenantInviteFields",
        "20260912112554_AddPayloadFlags",
        "20260912120127_AddQuickAccessLinks",
        "20260912120230_AddNotificationsAndCommentVerifiedAt",
        "20260912125359_AddProjectCaptureTextContent",
        "20260912193031_AddDeployAwareness",
        "20260915163653_FixPayloadFlagsJsonDefault",
        "20260916121926_AppUrlUniqueIndexIgnoresSoftDeleted",
        "20260916165610_AddDeviceLogins",
        "20260917220722_AddReplyIsAi",
        "20260917230734_AddCommentLanguage",
        "20260917234138_WidenUsageEventSource",
        "20260918004732_AddReplyAiAttribution",
        "20260918205719_SuggestionChangesRequestedAndNotifications",
        "20260921215630_AddCommentFieldsAndWorkspaceSettings",
    };

    private static readonly Regex RiskyOperation = new(
        @"\.(DropColumn|DropTable|RenameColumn|RenameTable|DropIndex|DropForeignKey|DropPrimaryKey|AlterColumn|Sql)\(",
        RegexOptions.Compiled
    );

    private static readonly Regex ApprovalMarker = new(
        @"//\s*DB-RULES:\s*(R2 contract|R3 backfill|index change|R4 constraint)\s+approved\s+\d{4}-\d{2}-\d{2}\s+by\s+\S+",
        RegexOptions.Compiled
    );

    private static readonly Regex UpToDownSlice = new(
        @"void Up\(.*?void Down\(",
        RegexOptions.Singleline | RegexOptions.Compiled
    );

    private static IEnumerable<string> MigrationFiles() =>
        Directory
            .EnumerateFiles(MigrationsDir, "*.cs")
            .Where(f =>
                !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !f.EndsWith("AppDbContextModelSnapshot.cs", StringComparison.Ordinal)
            );

    [Fact]
    public void HistoricalMigrationIdsAreFrozen()
    {
        var missing = Baseline
            .Where(id => !File.Exists(Path.Combine(MigrationsDir, id + ".cs")))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Frozen migration id(s) no longer exist on disk (renamed or deleted — see DB-RULES.md R10): "
                + string.Join(", ", missing)
        );
    }

    [Fact]
    public void NewMigrationsWithRiskyOperationsCarryApproval()
    {
        var violations = new List<string>();

        foreach (var file in MigrationFiles())
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (Baseline.Contains(id))
                continue; // historical migrations are frozen and exempt on purpose

            var content = File.ReadAllText(file);
            var upToDown = UpToDownSlice.Match(content);
            if (!upToDown.Success)
                continue;

            var offending = RiskyOperation.Match(upToDown.Value);
            if (!offending.Success)
                continue;

            if (!ApprovalMarker.IsMatch(content))
                violations.Add(
                    $"{Path.GetFileName(file)}: contains risky operation '{offending.Value.TrimEnd('(')}' without a DB-RULES approval marker"
                );
        }

        Assert.True(
            violations.Count == 0,
            "New migration(s) with risky operations missing an approval marker (see DB-RULES.md R2/R3/R13):\n"
                + string.Join("\n", violations)
        );
    }

    [Fact]
    public void ApprovalMarkerAndContractAttributeAgree()
    {
        var violations = new List<string>();

        foreach (var file in MigrationFiles())
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (Baseline.Contains(id))
                continue; // historical migrations are frozen and exempt on purpose

            var content = File.ReadAllText(file);
            var hasMarker = ApprovalMarker.IsMatch(content);
            var hasAttribute = content.Contains("[ContractMigration");

            if (hasMarker && !hasAttribute)
                violations.Add(
                    $"{Path.GetFileName(file)}: has approval marker but no [ContractMigration] attribute"
                );
            else if (!hasMarker && hasAttribute)
                violations.Add(
                    $"{Path.GetFileName(file)}: has [ContractMigration] attribute but no approval marker"
                );
        }

        Assert.True(
            violations.Count == 0,
            "DB-RULES approval marker and [ContractMigration] attribute must agree (see DB-RULES.md R7, docs/db/execution/DB-09-migration-apply-gate.md):\n"
                + string.Join("\n", violations)
        );
    }
}
