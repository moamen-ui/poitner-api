using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

public class SnapshotSanitizerTests
{
    [Fact]
    public void Sanitize_FormTag_ValueAttribute_RewrittenToMasked()
    {
        var input = "<input id=\"legacy-input\" type=\"text\" value=\"secret-text\">";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: true);
        Assert.Equal("<input id=\"legacy-input\" type=\"text\" value=\"•••\">", result);
    }

    [Fact]
    public void Sanitize_FormTag_EmptyValueAttribute_Stripped()
    {
        var input = "<input id=\"legacy-input\" type=\"text\" value=\"\">";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: true);
        Assert.Equal("<input id=\"legacy-input\" type=\"text\">", result);
    }

    [Fact]
    public void Sanitize_NonFormTag_ValueAttribute_Stripped()
    {
        var input = "<div id=\"d1\" value=\"forbidden\">hello</div>";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: true);
        Assert.Equal("<div id=\"d1\">hello</div>", result);
    }

    [Fact]
    public void Sanitize_SensitiveAttributes_Stripped()
    {
        var input = "<span id=\"t1\" data-token=\"abc\" data-user-id=\"42\" data-username=\"john\" data-email=\"a@b.com\" data-secret=\"shh\" data-value=\"val\" authorization=\"Bearer 1\" srcdoc=\"page\">hello</span>";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: true);
        Assert.Equal("<span id=\"t1\">hello</span>", result);
    }

    [Fact]
    public void Sanitize_NonSensitiveAttributes_Preserved()
    {
        var input = "<button id=\"b1\" type=\"button\" role=\"button\" aria-label=\"Save\" data-customer-name=\"Jane\">Save</button>";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: true);
        Assert.Equal("<button id=\"b1\" type=\"button\" role=\"button\" aria-label=\"Save\" data-customer-name=\"Jane\">Save</button>", result);
    }

    [Fact]
    public void Sanitize_CaptureTextFalse_ReplacesInnerTextWithMask()
    {
        var input = "<button id=\"cta\" type=\"submit\">Click me</button>";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: false);
        Assert.Equal("<button id=\"cta\" type=\"submit\">•••</button>", result);
    }

    [Fact]
    public void Sanitize_CaptureTextFalse_VoidElement_NoInnerToMask()
    {
        var input = "<input id=\"legacy-input\" type=\"text\" value=\"x\">";
        var result = SnapshotSanitizer.Sanitize(input, captureTextContent: false);
        Assert.Equal("<input id=\"legacy-input\" type=\"text\" value=\"•••\">", result);
    }

    [Fact]
    public void Sanitize_Idempotency()
    {
        var cases = new[]
        {
            "<input id=\"legacy-input\" type=\"text\" value=\"x\">",
            "<span id=\"t1\" data-token=\"abc\" data-user-id=\"42\">hello</span>",
            "<button id=\"cta\" type=\"submit\">Click me</button>",
            "<textarea value=\"confidential\">my feedback</textarea>",
        };

        foreach (var c in cases)
        {
            var pass1True = SnapshotSanitizer.Sanitize(c, captureTextContent: true);
            var pass2True = SnapshotSanitizer.Sanitize(pass1True, captureTextContent: true);
            Assert.Equal(pass1True, pass2True);

            var pass1False = SnapshotSanitizer.Sanitize(c, captureTextContent: false);
            var pass2False = SnapshotSanitizer.Sanitize(pass1False, captureTextContent: false);
            Assert.Equal(pass1False, pass2False);
        }
    }

    [Fact]
    public void Sanitize_MalformedInput_NoClosingBracket_ReturnsUnchangedWithoutThrowing()
    {
        var malformed = "<input id=\"q\" value=\"x\"";
        var result = SnapshotSanitizer.Sanitize(malformed, captureTextContent: true);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Sanitize_MalformedInput_OddQuotes_ReturnsUnchangedWithoutThrowing()
    {
        var malformed = "<input id=\"q\" value=\"x>";
        var result = SnapshotSanitizer.Sanitize(malformed, captureTextContent: true);
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Sanitize_MalformedInput_Oversized_ReturnsUnchangedWithoutThrowing()
    {
        var oversized = "<input " + new string('a', 4005) + ">";
        var result = SnapshotSanitizer.Sanitize(oversized, captureTextContent: true);
        Assert.Equal(oversized, result);
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsUnchangedWithoutThrowing()
    {
        Assert.Equal(string.Empty, SnapshotSanitizer.Sanitize(string.Empty, captureTextContent: true));
        Assert.Equal(string.Empty, SnapshotSanitizer.Sanitize(null, captureTextContent: true));
    }
}
