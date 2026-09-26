using Pointer.Application.Common;
using Pointer.Application.Resources;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Review finding #3: dedicated edge-case coverage for <see cref="WorkspaceNameRules.Validate"/> —
/// the shared rule behind both the rename path (<c>WorkspaceService.RenameAsync</c>) and the
/// signed-in new-workspace path (<c>WorkspaceCreationService</c>, DB-19 §3.4 task 6).
/// </summary>
public class WorkspaceNameRulesTests
{
    [Fact]
    public void Validate_Null_ReturnsNameRequired()
    {
        var error = WorkspaceNameRules.Validate(null, out var trimmed);

        Assert.Equal(MessageKeys.Workspace.NameRequired, error);
        Assert.Equal(string.Empty, trimmed);
    }

    [Fact]
    public void Validate_Empty_ReturnsNameRequired()
    {
        var error = WorkspaceNameRules.Validate("", out _);

        Assert.Equal(MessageKeys.Workspace.NameRequired, error);
    }

    [Fact]
    public void Validate_WhitespaceOnly_ReturnsNameRequired()
    {
        var error = WorkspaceNameRules.Validate("   ", out var trimmed);

        Assert.Equal(MessageKeys.Workspace.NameRequired, error);
        Assert.Equal(string.Empty, trimmed);
    }

    [Fact]
    public void Validate_TrimsSurroundingWhitespace()
    {
        var error = WorkspaceNameRules.Validate("  Acme Client  ", out var trimmed);

        Assert.Null(error);
        Assert.Equal("Acme Client", trimmed);
    }

    [Fact]
    public void Validate_ExactlyMaxLength_IsValid()
    {
        var name = new string('a', WorkspaceNameRules.MaxLength);

        var error = WorkspaceNameRules.Validate(name, out var trimmed);

        Assert.Null(error);
        Assert.Equal(name, trimmed);
    }

    [Fact]
    public void Validate_OneOverMaxLength_ReturnsNameTooLong()
    {
        var name = new string('a', WorkspaceNameRules.MaxLength + 1);

        var error = WorkspaceNameRules.Validate(name, out _);

        Assert.Equal(MessageKeys.Workspace.NameTooLong, error);
    }

    /// <summary>Trimming happens before the length check, so surrounding whitespace that would
    /// push the raw length over the limit must not fail a name whose trimmed form fits.</summary>
    [Fact]
    public void Validate_LengthCheckAppliesToTrimmedValue()
    {
        var name = "  " + new string('a', WorkspaceNameRules.MaxLength) + "  ";

        var error = WorkspaceNameRules.Validate(name, out var trimmed);

        Assert.Null(error);
        Assert.Equal(WorkspaceNameRules.MaxLength, trimmed.Length);
    }

    /// <summary>
    /// A lone \t or \n is whitespace to <c>string.Trim()</c> and trims away to an empty string
    /// (covered by <see cref="Validate_WhitespaceOnly_ReturnsNameRequired"/>), so these cases embed
    /// the control character between other characters — the only way it survives trimming to reach
    /// the <c>char.IsControl</c> check.
    /// </summary>
    [Theory]
    [InlineData("Valid\u0000Name")]
    [InlineData("Valid\u0007Name")]
    [InlineData("Valid\tName")]
    [InlineData("Valid\nName")]
    [InlineData("Valid\u0001Name")]
    public void Validate_EmbeddedControlCharacters_ReturnsNameInvalid(string raw)
    {
        var error = WorkspaceNameRules.Validate(raw, out _);

        Assert.Equal(MessageKeys.Workspace.NameInvalid, error);
    }

    [Fact]
    public void Validate_OrdinaryUnicodeName_IsValid()
    {
        var error = WorkspaceNameRules.Validate("Café Müller 株式会社", out var trimmed);

        Assert.Null(error);
        Assert.Equal("Café Müller 株式会社", trimmed);
    }
}
