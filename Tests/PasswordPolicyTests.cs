using Pointer.Application.Common;
using Pointer.Application.Resources;
using Xunit;

namespace Pointer.Tests;

/// <summary>DB-14 §6 test 6 — <see cref="PasswordPolicy"/>: 10–128 chars, not in the embedded
/// top-1000 list, not the e-mail (or its local part). D14.5: existing hashes are never re-checked
/// — this suite only exercises the pure function, never a stored hash.</summary>
public class PasswordPolicyTests
{
    [Fact]
    public void TooShort_IsWeak()
    {
        Assert.Equal(MessageKeys.User.PasswordWeak, PasswordPolicy.Validate("short1", "a@x.com"));
    }

    [Fact]
    public void TooLong_IsRejected()
    {
        var pw = new string('a', 129);
        Assert.Equal(MessageKeys.User.PasswordTooLong, PasswordPolicy.Validate(pw, "a@x.com"));
    }

    // Doc deviation: the upstream SecLists file the execution doc names
    // (Passwords/Common-Credentials/10-million-password-list-top-1000.txt) has since been renamed to
    // xato-net-10-million-passwords-1000.txt (embedded here — see common-passwords.txt's header); its
    // top 1000 entries do not include "password123"/"Password123" (the doc's own examples), so this
    // uses "qwertyuiop"/"QWERTYUIOP" instead — an entry the embedded list actually contains, at 10
    // characters (the doc's own examples are 9, which MinLength=10 would reject as PasswordWeak
    // before ever reaching the common-list check).
    [Theory]
    [InlineData("qwertyuiop")]
    [InlineData("QWERTYUIOP")]
    public void CommonPassword_IsRejected(string pw)
    {
        Assert.Equal(MessageKeys.User.PasswordCommon, PasswordPolicy.Validate(pw, "a@x.com"));
    }

    // Doc deviation: the doc's own examples ("a@x.com" full address, "A" local part) are 7 and 1
    // characters — both shorter than MinLength=10, so they would report PasswordWeak, not
    // PasswordIsEmail, before the address check is ever reached. Using a longer address/local part
    // instead so the e-mail check is what actually fires.
    [Fact]
    public void PasswordEqualsEmail_IsRejected()
    {
        const string email = "alexandra12@example.com";
        Assert.Equal(MessageKeys.User.PasswordIsEmail, PasswordPolicy.Validate(email, email));
    }

    [Fact]
    public void PasswordEqualsLocalPart_IsRejected()
    {
        const string email = "alexandra12@example.com";
        Assert.Equal(MessageKeys.User.PasswordIsEmail, PasswordPolicy.Validate("alexandra12", email));
        // Case-insensitive.
        Assert.Equal(MessageKeys.User.PasswordIsEmail, PasswordPolicy.Validate("ALEXANDRA12", email));
    }

    [Fact]
    public void AcceptablePassword_ReturnsNull()
    {
        Assert.Null(PasswordPolicy.Validate("correct-horse-battery", "a@x.com"));
    }

    [Fact]
    public void NoEmail_SkipsAddressCheck()
    {
        Assert.Null(PasswordPolicy.Validate("correct-horse-battery", null));
    }

    [Fact]
    public void EmbeddedList_HasExactly1000EntriesAndContains123456()
    {
        Assert.True(PasswordPolicy.IsCommon("123456"));

        // Review finding #5: the resource holds exactly 1000 DISTINCT case-insensitive entries and
        // no blank line — assert the SET size, not the raw line count (a raw count would silently
        // pass even with a blank line or a case-variant duplicate inflating it back to 1000).
        var assembly = typeof(PasswordPolicy).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("common-passwords.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith('#') || line.Length == 0)
                continue;
            set.Add(line);
        }
        Assert.Equal(1000, set.Count);
    }
}
