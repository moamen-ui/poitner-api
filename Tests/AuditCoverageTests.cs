using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Pointer.API.Auth;
using Pointer.API.Controllers;
using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §6 test 1 — the coverage reflection enforcer. The exhaustive fact (every mutating
/// action on an admin controller + Auth/Me/Demo/ExportImport carries exactly one of
/// <c>[Audited]</c>/<c>[NoAudit]</c>) ships DISABLED until DB-12 PART 2 lands the attributes on
/// the ~70 actions (§5 tasks 10–11); the facts that already hold today are active: any attribute
/// that DOES exist must be well-formed.
/// </summary>
public class AuditCoverageTests
{
    private static readonly string[] ExtraAuditedControllers =
    [
        "AuthController",
        "MeController",
        "DemoController",
        "ExportImportController",
    ];

    private static IEnumerable<Type> AuditedSurfaces() =>
        typeof(AuthController)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && typeof(ControllerBase).IsAssignableFrom(t)
                && (
                    t.Namespace?.StartsWith(
                        "Pointer.API.Controllers.Admin",
                        StringComparison.Ordinal
                    ) == true
                    || ExtraAuditedControllers.Contains(t.Name)
                )
            );

    private static readonly string[] MutatingVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Real mutating-verb detection via the HTTP-method-provider attributes' <c>HttpMethods</c>
    /// (e.g. <c>[HttpPost]</c> implements <see cref="IActionHttpMethodProvider"/> and reports
    /// <c>"POST"</c>) — NOT the attribute's CLR type name, which for <c>[HttpPost]</c> is
    /// <c>"HttpPostAttribute"</c> and can never equal a bare verb string.
    /// </summary>
    private static bool IsMutatingAction(MethodInfo m) =>
        m.GetCustomAttributes(inherit: false)
            .OfType<IActionHttpMethodProvider>()
            .SelectMany(a => a.HttpMethods)
            .Any(v => MutatingVerbs.Contains(v, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// §3.8 / review finding #10: EVERY action (any verb — GETs included) of the
    /// <see cref="ExtraAuditedControllers"/> set (Auth, Me, Demo, ExportImport) carries the
    /// attribute, not just its mutating ones (this also subsumes the old ExportImportController
    /// GET special-case); every OTHER audited surface (the <c>Pointer.API.Controllers.Admin</c>
    /// namespace) only requires it on mutating actions.
    /// </summary>
    private static bool RequiresAttribute(MethodInfo m) =>
        (m.DeclaringType is not null && ExtraAuditedControllers.Contains(m.DeclaringType.Name))
        || IsMutatingAction(m);

    [Fact]
    public void IsMutatingAction_DetectsRealHttpVerbAttributes()
    {
        // AuthController.Login is [HttpPost] — HttpMethods = ["POST"]. Before the fix,
        // IsMutatingAction compared the attribute's CLR TYPE NAME ("HttpPostAttribute") against
        // the verb list ("HttpPost", …) and was always false.
        var login =
            typeof(AuthController).GetMethod(nameof(AuthController.Login))
            ?? throw new InvalidOperationException("AuthController.Login not found");
        var me =
            typeof(AuthController).GetMethod(nameof(AuthController.Me))
            ?? throw new InvalidOperationException("AuthController.Me not found");

        Assert.True(IsMutatingAction(login), "[HttpPost] Login must be detected as mutating.");
        Assert.False(IsMutatingAction(me), "[HttpGet] Me must NOT be detected as mutating.");
    }

    /// <summary>
    /// Proves the enumeration behind the (still-skipped) exhaustive fact below actually finds a
    /// realistic number of candidates. Before the fix, <c>IsMutatingAction</c> was always false and
    /// this count would have collapsed to (at most) the two ExportImportController GET exports.
    /// </summary>
    [Fact]
    public void MutatingActionEnumeration_FindsOverFortyCandidates()
    {
        var candidates = AuditedSurfaces()
            .SelectMany(t =>
                t.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                )
            )
            .Where(RequiresAttribute)
            .ToList();

        Assert.True(
            candidates.Count > 40,
            $"Expected more than 40 mutating-action candidates across the audited surfaces, found {candidates.Count}."
        );
    }

    [Fact]
    public void AuditedActionStrings_AreInTheCatalogue()
    {
        var offenders = AuditedSurfaces()
            .SelectMany(t =>
                t.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                )
            )
            .Select(m =>
                (Method: m, Attribute: m.GetCustomAttribute<AuditedAttribute>(inherit: false))
            )
            .Where(x => x.Attribute is not null && !AuditActions.All.Contains(x.Attribute.Action))
            .Select(x => $"{x.Method.DeclaringType?.Name}.{x.Method.Name}: '{x.Attribute!.Action}'")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "[Audited] action string(s) not in AuditActions.All (use the constant, never a literal):\n"
                + string.Join("\n", offenders)
        );
    }

    [Fact]
    public void NoAuditReasons_AreNonEmpty()
    {
        var offenders = AuditedSurfaces()
            .SelectMany(t =>
                t.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                )
            )
            .Select(m =>
                (Method: m, Attribute: m.GetCustomAttribute<NoAuditAttribute>(inherit: false))
            )
            .Where(x => x.Attribute is not null && string.IsNullOrWhiteSpace(x.Attribute.Reason))
            .Select(x => $"{x.Method.DeclaringType?.Name}.{x.Method.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "[NoAudit] without a non-empty reason (an exemption is a written-down decision, not an oversight):\n"
                + string.Join("\n", offenders)
        );
    }

    [Fact]
    public void AuditReads_Themselves_CarryNoAudit()
    {
        var reads = typeof(Pointer.API.Controllers.Admin.AuditController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpGetAttribute>(inherit: false) is not null)
            .ToList();

        Assert.NotEmpty(reads);
        Assert.All(
            reads,
            m =>
                Assert.Equal(
                    "read of the audit log itself",
                    m.GetCustomAttribute<NoAuditAttribute>()?.Reason
                )
        );
    }

    /// <summary>
    /// R10/R17: the reserved action strings are the vocabulary for later docs (DB-11b/c/d, DB-13,
    /// DB-14) — pin them so no later PR invents a second spelling.
    /// </summary>
    [Fact]
    public void ReservedActionStrings_AreInTheCatalogue()
    {
        string[] reserved =
        [
            AuditActions.AuthWorkspaceSwitched,
            AuditActions.MemberLeft,
            AuditActions.IdentityEraseRequested,
            AuditActions.IdentityErased,
            AuditActions.AuthEmailChangeRequested,
            AuditActions.AuthEmailChanged,
            AuditActions.AuthEmailVerified,
            AuditActions.ImpersonationStarted,
            AuditActions.ImpersonationEnded,
        ];
        Assert.All(reserved, s => Assert.Contains(s, AuditActions.All));
    }

    /// <summary>
    /// The full §6 test 1 contract, widened per review finding #10: every action (any verb) of
    /// AuthController / MeController / DemoController / ExportImportController, plus every
    /// mutating action on a controller under Pointer.API.Controllers.Admin, carries exactly one of
    /// the two attributes. Enable when DB-12 PART 2 lands (§5 tasks 10–11).
    /// </summary>
    [Fact(
        Skip = "DB-12 PART 2: enable when [Audited]/[NoAudit] ship on all ~70 actions (§5 tasks 10–11)."
    )]
    public void EveryMutatingAction_CarriesExactlyOneAttribute()
    {
        var missing = AuditedSurfaces()
            .SelectMany(t =>
                t.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                )
            )
            .Where(RequiresAttribute)
            .Where(m =>
                m.GetCustomAttribute<AuditedAttribute>(inherit: false) is null
                && m.GetCustomAttribute<NoAuditAttribute>(inherit: false) is null
            )
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Mutating action(s) without [Audited] or [NoAudit] (DB-RULES R17 — a new endpoint that changes state and lacks the attribute does not merge):\n"
                + string.Join("\n", missing)
        );
    }
}
