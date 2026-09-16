using System.Reflection;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The stamp that lets `doctor` tell a developer their installed skill copy is older than the
/// server's.
///
/// The placement rule is the fragile part and the reason these assertions exist: both .md skills
/// open with YAML frontmatter that AI tools parse, so the stamp must land on the first line AFTER
/// the closing `---`, located by scanning — never on line 1, and never at a fixed line number.
/// </summary>
public class ServedSkillVersionTests
{
    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pointer.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "API", "wwwroot");
    }

    private const string Placeholder = "<POINTER_SKILL_VERSION>";

    [Theory]
    [InlineData("skill.md")]
    [InlineData("pointer-init.md")]
    public void MarkdownSkill_CarriesTheStamp_ImmediatelyAfterItsFrontmatter(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(WwwRoot(), file));

        Assert.Equal("---", lines[0].Trim());

        // Locate the closing --- by scanning, exactly as any parser must.
        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "---") continue;
            close = i;
            break;
        }
        Assert.True(close > 0, $"{file} has no closing frontmatter delimiter");

        Assert.Equal($"<!-- pointer-skill-version: {Placeholder} -->", lines[close + 1].Trim());

        // The stamp must never precede the frontmatter — that is what breaks an AI tool's parse.
        Assert.DoesNotContain("pointer-skill-version", lines[0]);
    }

    [Theory]
    [InlineData("install.sh")]
    [InlineData("pointer.sh")]
    public void ShellScript_CarriesTheStamp_OnLineTwo(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(WwwRoot(), file));

        Assert.StartsWith("#!", lines[0]);
        Assert.Equal($"# pointer-skill-version: {Placeholder}", lines[1].Trim());
    }

    [Fact]
    public void ContentStamp_ChangesWhenASkillFileChanges_AndIsStableOtherwise()
    {
        var a = SkillVersionResolver.WithContentStamp("0.0.0-dev", new[] { "skill A", "init A" });
        var same = SkillVersionResolver.WithContentStamp("0.0.0-dev", new[] { "skill A", "init A" });
        var edited = SkillVersionResolver.WithContentStamp("0.0.0-dev", new[] { "skill A (edited)", "init A" });

        Assert.Equal(a, same);
        Assert.NotEqual(a, edited);
        Assert.StartsWith("0.0.0-dev+skills.", a);
        Assert.Equal("0.0.0-dev+skills.".Length + 12, a.Length);
    }

    [Fact]
    public void ContentStamp_ReplacesExistingBuildMetadata_SoTheStampHasOnePlus()
    {
        var stamped = SkillVersionResolver.WithContentStamp("1.2.3+abc123", new[] { "x" });
        Assert.StartsWith("1.2.3+skills.", stamped);
        Assert.Equal(1, stamped.Count(ch => ch == '+'));
    }

    [Fact]
    public void Resolver_PrefersTheConfiguredValue_SoSkillEditsCanShipWithoutCode()
    {
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SkillVersionResolver.ConfigKey] = "2026.09.12" })
            .Build();

        Assert.Equal("2026.09.12", SkillVersionResolver.Resolve(configured));
    }

    [Fact]
    public void Resolver_FallsBackToTheAssemblyVersion_NotAnEmptyString()
    {
        var empty = new ConfigurationBuilder().Build();

        var resolved = SkillVersionResolver.Resolve(empty);

        Assert.False(string.IsNullOrWhiteSpace(resolved));
    }

    [Fact]
    public void Resolver_IgnoresABlankOverride()
    {
        // An env var set to "" is a common deployment slip; treating it as a real version would
        // stamp every served file with nothing and make every install look stale.
        var blank = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SkillVersionResolver.ConfigKey] = "   " })
            .Build();

        Assert.False(string.IsNullOrWhiteSpace(SkillVersionResolver.Resolve(blank)));
        Assert.NotEqual("   ", SkillVersionResolver.Resolve(blank));
    }
}
