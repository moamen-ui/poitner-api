using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §3.5 — the before/after whitelist sanitiser. Review finding #6 (LOW): a null value
/// (the static type is non-nullable, but nothing at runtime stops a caller from putting one in a
/// dictionary) must be treated as empty rather than NRE'ing inside <c>value.Length</c>.
/// </summary>
public class AuditFieldsTests
{
    [Fact]
    public void Sanitize_NullValue_TreatedAsEmpty_DoesNotThrow()
    {
        var dict = new Dictionary<string, string> { ["name"] = null! };

        var result = AuditFields.Sanitize(dict);

        Assert.Equal(string.Empty, result["name"]);
    }

    [Fact]
    public void Sanitize_DropsUnknownKeys_TruncatesLongValues()
    {
        var dict = new Dictionary<string, string>
        {
            ["name"] = new string('n', 250),
            ["email"] = "someone@example.com", // not whitelisted
        };

        var result = AuditFields.Sanitize(dict);

        Assert.False(result.ContainsKey("email"));
        Assert.Equal(200, result["name"].Length);
    }

    [Fact]
    public void Sanitize_NullOrEmptyInput_ReturnsEmptyDictionary()
    {
        Assert.Empty(AuditFields.Sanitize(null));
        Assert.Empty(AuditFields.Sanitize(new Dictionary<string, string>()));
    }
}
