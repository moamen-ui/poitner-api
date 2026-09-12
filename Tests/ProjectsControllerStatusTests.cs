using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Controllers.Admin;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING: a Forbidden result must never leave a controller as 400.
///
/// ProjectsController.Create handled NotFound and Conflict and let everything else fall through to
/// BadRequest. ProjectService.CreateAsync returns Forbidden for a super admin and for a
/// quick-access account, so both arrived at the client as a validation error carrying a permission
/// message. The CLI, reasonably, treated 400 as "your input was wrong", printed the server text and
/// exited 1 instead of the documented 3 — and no client could distinguish "not allowed" from
/// "malformed".
///
/// This is a source-level check rather than a behavioural one because the mapping lives in the
/// action bodies, not in anything injectable; the value is in failing when a new action is added
/// without the line.
/// </summary>
public class ProjectsControllerStatusTests
{
    private static string ControllerSource()
    {
        // Walk up from the test assembly to the repo root, then to the controller.
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pointer.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "API", "Controllers", "Admin", "ProjectsController.cs");
        Assert.True(File.Exists(path), $"expected the controller at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryAction_ThatFallsBackToBadRequest_AlsoHandlesForbidden()
    {
        var source = ControllerSource();

        // Split into action bodies on the public action signatures.
        var parts = source.Split("public async Task<IActionResult>", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.NotEmpty(parts);

        var offenders = parts
            .Where(body => body.Contains("BadRequest(result)") && !body.Contains("IsForbidden"))
            .Select(body => body.Split('(')[0].Trim())
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"these actions turn a Forbidden result into 400: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Create_MapsForbidden_To403()
    {
        var source = ControllerSource();
        var create = source.Split("public async Task<IActionResult> Create(")[1].Split("[Http")[0];

        Assert.Contains("IsForbidden", create);
        Assert.Contains("Status403Forbidden", create);
    }
}
