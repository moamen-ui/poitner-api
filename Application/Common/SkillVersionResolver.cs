using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Appends a content hash of the served skill files as semver build metadata:
    /// <c>0.0.0-dev+skills.3f9c21ab8d0e</c>. The CLI's <c>update</c>/<c>doctor</c> compare stamps by
    /// exact string, so a stamp that never changes hides every skill edit — which is what happened in
    /// production, where the image has no git metadata and the assembly version stays "0.0.0-dev".
    /// Any existing build metadata is dropped first (semver allows a single '+').
    /// </summary>
    public static string WithContentStamp(string version, IEnumerable<string> contents)
    {
        var core = version.Split('+', 2)[0].Trim();
        if (string.IsNullOrEmpty(core)) core = "0.0.0-dev";
        using var sha = SHA256.Create();
        foreach (var content in contents)
        {
            var bytes = Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n"));
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hex = Convert.ToHexString(sha.Hash!).ToLowerInvariant()[..12];
        return $"{core}+skills.{hex}";
    }
}
