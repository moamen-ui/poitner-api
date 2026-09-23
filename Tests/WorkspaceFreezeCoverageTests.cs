using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.API.Controllers;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-18 §3.5/§6 test 7 — pins the exact set of controllers/actions carrying
/// <see cref="AllowWhenWorkspacePausedAttribute"/> to the list DB-18 §3.5 names. A future PR that
/// adds or removes a placement must update this test in the same change (mirrors
/// <see cref="AuditCoverageTests"/>'s reflection-enumeration shape).
/// </summary>
public class WorkspaceFreezeCoverageTests
{
    private static IEnumerable<Type> AllControllers() =>
        typeof(AuthController)
            .Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

    [Fact]
    public void ClassLevel_ExactlyAuthMeAndMfaControllers()
    {
        var classLevel = AllControllers()
            .Where(t =>
                t.GetCustomAttribute<AllowWhenWorkspacePausedAttribute>(inherit: false) != null
            )
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "AuthController", "MeController", "MfaController" }, classLevel);
    }

    [Fact]
    public void MethodLevel_ExactlyTheDocumentedList()
    {
        var methodLevel = AllControllers()
            .SelectMany(t =>
                t.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    )
                    .Where(m =>
                        m.GetCustomAttribute<AllowWhenWorkspacePausedAttribute>(inherit: false)
                        != null
                    )
                    .Select(m => $"{t.Name}.{m.Name}")
            )
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var expected = new[]
        {
            "AuthController.Me",
            "EventsController.RecordEvent",
            "InvitesController.Revoke",
            "InvitesController.RotateQuickLink",
            "UsersController.Delete",
            "UsersController.Update",
            "WorkspaceController.CancelDeletion",
            "WorkspaceController.RequestDeletion",
            "WorkspaceController.Resume",
        }
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, methodLevel);
    }

    [Fact]
    public void AllowKeySessions_ExactlyAuthMeAndRecordEvent()
    {
        var allowKeySessions = AllControllers()
            .SelectMany(t =>
                t.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    )
                    .Select(m =>
                        (
                            Type: t,
                            Method: m,
                            Attr: m.GetCustomAttribute<AllowWhenWorkspacePausedAttribute>(
                                inherit: false
                            )
                        )
                    )
                    .Where(x => x.Attr?.AllowKeySessions == true)
                    .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            )
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "AuthController.Me", "EventsController.RecordEvent" },
            allowKeySessions
        );
    }

    [Fact]
    public void EventsController_RecordEvent_IsMethodLevelOnly_NotClassLevel()
    {
        // Opus LOW: a class-level exemption on EventsController would also open
        // GET api/admin/events/summary (Admin-only) to key sessions.
        var type = typeof(Pointer.API.Controllers.EventsController);
        Assert.Null(type.GetCustomAttribute<AllowWhenWorkspacePausedAttribute>(inherit: false));
        var recordEvent = type.GetMethod(
            nameof(Pointer.API.Controllers.EventsController.RecordEvent)
        )!;
        Assert.NotNull(
            recordEvent.GetCustomAttribute<AllowWhenWorkspacePausedAttribute>(inherit: false)
        );
        var summary = type.GetMethod(nameof(Pointer.API.Controllers.EventsController.GetSummary))!;
        Assert.Null(summary.GetCustomAttribute<AllowWhenWorkspacePausedAttribute>(inherit: false));
    }
}
