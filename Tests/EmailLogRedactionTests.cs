using System.Reflection;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// GLM review F7 — BrevoEmailSender/SmtpEmailSender used to log the raw recipient address
/// ({To}) on every send attempt, which meant real e-mails ended up in structured stdout logs
/// and (once a Sentry DSN is configured) in breadcrumbs. EmailLogRedaction.Pseudonymize is the
/// fix: callers now log its output instead of the raw address. It's `internal`, so these tests
/// reach it via reflection rather than adding an InternalsVisibleTo just for this.
/// </summary>
public class EmailLogRedactionTests
{
    private static string Pseudonymize(string? email)
    {
        var type = Type.GetType(
            "Pointer.Infrastructure.Email.EmailLogRedaction, Pointer.Infrastructure"
        );
        Assert.NotNull(type);
        var method = type!.GetMethod("Pseudonymize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { email })!;
    }

    [Theory]
    [InlineData("alice@example.com", "al…@example.com")]
    [InlineData("a@example.com", "a…@example.com")]
    [InlineData("bob.smith+tag@sub.example.co", "bo…@sub.example.co")]
    public void Pseudonymize_KeepsDomainAndFirstTwoLocalChars_HidesRest(
        string input,
        string expected
    )
    {
        Assert.Equal(expected, Pseudonymize(input));
    }

    [Fact]
    public void Pseudonymize_NeverReturnsTheFullAddress()
    {
        const string email = "someone.private@company-domain.example";

        var result = Pseudonymize(email);

        Assert.NotEqual(email, result);
        Assert.DoesNotContain("someone.private", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public void Pseudonymize_HandlesMissingOrMalformedInput_WithoutThrowing(string? input)
    {
        var result = Pseudonymize(input);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}
