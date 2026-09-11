namespace Pointer.Tests;

using System.Text;
using System.Text.RegularExpressions;
using Pointer.Application.Common;
using Xunit;

public class PayloadFlagDetectorTests
{
    // ── 1. openai_key ───────────────────────────────────────────────

    [Fact]
    public void OpenAiKey_Positive_Matches()
    {
        var input = "Found API key sk-abcdefghijklmnopqrstuvwxyz1234567890 in config file";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("openai_key", flags);
        Assert.Single(flags);
    }

    [Fact]
    public void OpenAiKey_Negative_DoesNotMatch()
    {
        var input = "The prefix sk-short is not long enough to be an openai key";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("openai_key", flags);
    }

    // ── 2. aws_access_key ───────────────────────────────────────────

    [Fact]
    public void AwsAccessKey_Positive_Matches()
    {
        var input = "AWS credential AKIAIOSFODNN7EXAMPLE detected in environment config";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("aws_access_key", flags);
        Assert.Single(flags);
    }

    [Fact]
    public void AwsAccessKey_Negative_DoesNotMatch()
    {
        // Lowercase or invalid length must not match
        var input = "AKIAiosfodnn7example AKIA12345 not an access key";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("aws_access_key", flags);
    }

    // ── 3. github_token ─────────────────────────────────────────────

    [Theory]
    [InlineData("ghp_123456789012345678901234567890123456")]
    [InlineData("gho_123456789012345678901234567890123456")]
    [InlineData("ghu_123456789012345678901234567890123456")]
    [InlineData("ghs_123456789012345678901234567890123456")]
    [InlineData("ghr_123456789012345678901234567890123456")]
    public void GitHubToken_Positive_Matches(string token)
    {
        var input = $"Git credential {token} committed to repository";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("github_token", flags);
        Assert.Single(flags);
    }

    [Fact]
    public void GitHubToken_Negative_DoesNotMatch()
    {
        var input = "ghp_tooshort and ghx_123456789012345678901234567890123456 are invalid";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("github_token", flags);
    }

    // ── 4. pointer_key ──────────────────────────────────────────────

    [Fact]
    public void PointerKey_Positive_Matches()
    {
        var input = "Connecting with pointer key ptr_123456789012345678901234 to server";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("pointer_key", flags);
        Assert.Single(flags);
    }

    [Fact]
    public void PointerKey_Negative_DoesNotMatch()
    {
        var input = "Short key ptr_12345 should not trigger pointer key flag";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("pointer_key", flags);
    }

    // ── 5. jwt ──────────────────────────────────────────────────────

    [Fact]
    public void Jwt_Positive_Matches()
    {
        var input =
            "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("jwt", flags);
    }

    [Fact]
    public void Jwt_Negative_DoesNotMatch()
    {
        var input = "eyJshort.abc.def is not a valid JWT format";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("jwt", flags);
    }

    // ── 6. private_key_block ────────────────────────────────────────

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void PrivateKeyBlock_Positive_Matches(string header)
    {
        var input = $"Certificate bundle:\n{header}\nMIIEowIBAAKCAQEA...\n-----END PRIVATE KEY-----";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("private_key_block", flags);
    }

    [Fact]
    public void PrivateKeyBlock_Negative_DoesNotMatch()
    {
        var input = "-----BEGIN PUBLIC KEY-----\n-----BEGIN CERTIFICATE-----";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("private_key_block", flags);
    }

    // ── 7. long_base64 ──────────────────────────────────────────────

    [Fact]
    public void LongBase64_Positive_Matches()
    {
        var base64 = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWZnaGlqa2xtbm9wcXJzdHV2d3l6MTIzNDU2Nzg5MA==";
        var input = $"Payload data: {base64} in body";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("long_base64", flags);
    }

    [Fact]
    public void LongBase64_Negative_DoesNotMatch()
    {
        var input = "Short base64 VGhpcyBpcyBhIHNob3J0IHN0cmluZw== in comment";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("long_base64", flags);
    }

    // ── 8. script_tag ───────────────────────────────────────────────

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("<SCRIPT src=\"https://malicious.example.com/payload.js\"></SCRIPT>")]
    [InlineData("<  script  type=\"text/javascript\">console.log('leak');</script>")]
    public void ScriptTag_Positive_Matches(string snippet)
    {
        var input = $"Injected tag: {snippet}";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("script_tag", flags);
    }

    [Fact]
    public void ScriptTag_Negative_DoesNotMatch()
    {
        var input = "Check out the script file in scripts/deploy.sh or description tag <description>valid</description>";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("script_tag", flags);
    }

    // ── 9. pipe_to_shell ────────────────────────────────────────────

    [Theory]
    [InlineData("curl -fsSL https://example.com/install.sh | bash")]
    [InlineData("wget -qO- https://example.com/setup | sh")]
    [InlineData("curl https://pointer.dev/install | zsh")]
    [InlineData("curl -sL https://example.com/script.sh |   bash")]
    public void PipeToShell_Positive_Matches(string command)
    {
        var input = $"To install, run: {command} in your terminal";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("pipe_to_shell", flags);
    }

    [Fact]
    public void PipeToShell_Negative_DoesNotMatch()
    {
        var input = "Run curl https://example.com/api/data.json or echo hello | grep world";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.DoesNotContain("pipe_to_shell", flags);
    }

    // ── 10. password_assignment ─────────────────────────────────────

    [Theory]
    [InlineData("password: SuperSecretPassword123")]
    [InlineData("passwd = MySecurePasswd456")]
    [InlineData("secret: HighEntropySecretToken789")]
    [InlineData("token = VeryLongAuthToken999")]
    [InlineData("PASSWORD = UppercaseSecret123")]
    [InlineData("TOKEN: CaseInsensitiveToken999")]
    public void PasswordAssignment_Positive_Matches(string assignment)
    {
        var input = $"Configuration leak: {assignment} in comments";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("password_assignment", flags);
    }

    [Theory]
    [InlineData("password: 123")] // less than 8 chars
    [InlineData("secret = abc")] // less than 8 chars
    [InlineData("Please update your password before logging in.")] // no : or =
    [InlineData("username: adminuser")] // key not in pattern
    public void PasswordAssignment_Negative_DoesNotMatch(string text)
    {
        var flags = PayloadFlagDetector.Detect(text);

        Assert.DoesNotContain("password_assignment", flags);
    }

    // ── Normal 5 KB comment matches nothing ─────────────────────────

    [Fact]
    public void NormalComment_5KB_MatchesNothing()
    {
        var sb = new StringBuilder();
        var paragraph =
            "The navigation bar on the settings page has inconsistent vertical alignment when viewed on mobile screens. "
            + "Specifically, the avatar dropdown overlaps slightly with the notification bell icon when the viewport width is below 768px. "
            + "We should update the CSS flex layout to ensure proper spacing between header controls. "
            + "Additionally, let's verify that the dark mode background color matches the design tokens defined in the style guide. "
            + "The typography hierarchy should also be reviewed for heading levels h1 through h3 across all dashboard pages.\n\n";

        while (sb.Length < 5120)
        {
            sb.Append(paragraph);
        }

        var comment5Kb = sb.ToString();
        Assert.True(Encoding.UTF8.GetByteCount(comment5Kb) >= 5120);

        var flags = PayloadFlagDetector.Detect(comment5Kb);
        Assert.Empty(flags);
    }

    // ── Regex timeout safety ────────────────────────────────────────

    [Fact]
    public void RegexTimeout_DoesNotThrow_ReturnsEmpty()
    {
        // Intentionally catastrophic backtracking pattern with 1ms timeout
        var timeoutRegex = new Regex(
            @"^(([a-z])+.)+[A-Z]([a-z])+$",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(1)
        );

        var customPatterns = new (string Name, Regex Rx)[] { ("catastrophic", timeoutRegex) };
        var maliciousInput = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!";

        // Must not throw RegexMatchTimeoutException; returns empty list
        var flags = PayloadFlagDetector.Detect(maliciousInput, customPatterns);
        Assert.Empty(flags);
    }

    // ── Output surface rules: Never leak secret contents ────────────

    [Fact]
    public void OutputSurface_ContainsOnlyPatternNames_NeverMatchedSecrets()
    {
        var rawSecret = "sk-abcdefghijklmnopqrstuvwxyz1234567890";
        var input = $"Found key {rawSecret} in code";
        var flags = PayloadFlagDetector.Detect(input);

        Assert.Contains("openai_key", flags);

        // Verify that the detector output NEVER exposes the secret text or offset
        foreach (var flag in flags)
        {
            Assert.DoesNotContain(rawSecret, flag);
            Assert.Contains(flag, PayloadFlagDetector.Patterns.Select(p => p.Name));
        }
    }

    // ── Multiple patterns & distinct flags ──────────────────────────

    [Fact]
    public void MultiplePatterns_ReturnsAllMatchedNames_Distinct()
    {
        var input =
            "Key 1: sk-abcdefghijklmnopqrstuvwxyz1234567890\n"
            + "Key 2: sk-zyxwvutsrqponmlkjihgfedcba0987654321\n"
            + "Tag: <script>alert(1)</script>";

        var flags = PayloadFlagDetector.Detect(input);

        Assert.Equal(2, flags.Count);
        Assert.Contains("openai_key", flags);
        Assert.Contains("script_tag", flags);
    }

    // ── Null / Empty / Whitespace handling ──────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    public void NullOrEmpty_ReturnsEmptyList(string? input)
    {
        var flags = PayloadFlagDetector.Detect(input);
        Assert.Empty(flags);
    }
}
