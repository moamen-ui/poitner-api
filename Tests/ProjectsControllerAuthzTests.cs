using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Pointer.API.Auth;
using Pointer.API.Controllers.Admin;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING #1: the prompt-emitting apply-queue action MUST stay admin-gated even though the
/// controller itself was broadened to plain [Authorize]. A non-admin hitting
/// /api/admin/projects/{key}/apply-queue must get 403 — enforced by an action-level Admin policy.
/// These reflection assertions fail loudly if that guard is ever removed.
/// </summary>
public class ProjectsControllerAuthzTests
{
    [Fact]
    public void Controller_IsAuthorize_ButNotClassLevelAdmin()
    {
        var type = typeof(ProjectsController);
        var authorize = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        // The controller is [Authorize] (any tenant member) — NOT class-level Admin.
        Assert.NotEmpty(authorize);
        Assert.DoesNotContain(authorize, a => a.Policy == Policies.Admin);
    }

    [Fact]
    public void ApplyQueue_Action_CarriesAdminPolicy()
    {
        var method = typeof(ProjectsController).GetMethod("ApplyQueue");
        Assert.NotNull(method);

        var authorize = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        // A non-admin is rejected (403) because THIS action requires the Admin policy.
        Assert.Contains(authorize, a => a.Policy == Policies.Admin);
    }

    [Fact]
    public void AdminSuggestionsController_IsAdminGated()
    {
        var type = typeof(SuggestionsController); // Admin namespace
        var authorize = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.Contains(authorize, a => a.Policy == Policies.Admin);
    }
}
