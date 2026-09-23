namespace Pointer.Application.Common;

/// <summary>Frozen identifiers (R10): a value written to usage_events.type is never renamed. Append new ones; never reuse.</summary>
public static class UsageEventTypes
{
    // F4 activation steps, in order. One-shot facts: emitted by the SERVER exactly once per workspace/project, never swept (DB-08 exclusion below).
    public const string DemoStarted = "demo_started"; // per demo workspace (DemoService.ProvisionAsync)
    public const string WorkspaceConverted = "workspace_converted"; // per workspace (DemoService.UpgradeAsync)
    public const string WidgetInstalled = "widget_installed"; // per project: first widget-status hit from a NON-localhost origin
    public const string FirstComment = "first_comment"; // per project (exists)
    public const string FirstApply = "first_apply"; // per project (exists)
    public static readonly string[] OneShotFacts =
    {
        DemoStarted,
        WorkspaceConverted,
        WidgetInstalled,
        FirstComment,
        FirstApply,
    };

    // Volume events (client-posted or server): rolled up daily, then swept after Retention:UsageEventsDays.
    public const string Installed = "installed";
    public const string DoctorRun = "doctor_run";
    public const string ApplyStarted = "apply_started";
    public const string ApplyFailed = "apply_failed";
    public const string WidgetLanguage = "widget_language";
    public static readonly string[] ClientPostable =
    {
        Installed,
        DoctorRun,
        ApplyStarted,
        ApplyFailed,
        WidgetLanguage,
    }; // = RecordEventValidator's list; test asserts equality
}
