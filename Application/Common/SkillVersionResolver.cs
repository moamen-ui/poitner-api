using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Pointer.Application.Common;

/// <summary>
/// The version stamped into every served skill/script, and reported as
/// <c>/api/meta.skillVersion</c>.
/// </summary>
/// <remarks>
/// Defaults to the API's assembly informational version so a deploy always moves it. It is
/// overridable via <c>Pointer:SkillVersion</c> because the skills are prose: fixing a wrong
/// instruction in skill.md should be able to invalidate every installed copy without shipping new
/// code.
///
/// Both readers — the served-file middleware and MetaController — must resolve it HERE. If they
/// computed it separately, `doctor` would compare a stamp against a different value and report
/// every install as stale (or, worse, none of them).
/// </remarks>
public static class SkillVersionResolver
{
    public const string ConfigKey = "Pointer:SkillVersion";

    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? "0.0.0-dev";
    }
}
