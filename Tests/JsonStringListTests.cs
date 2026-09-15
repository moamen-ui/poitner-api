using Pointer.Infrastructure.Mappings;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The jsonb → List&lt;string&gt; reader behind Comment/Reply.PayloadFlags and Plan.FeatureBullets.
/// Production had 104 comments whose payload_flags held the migration default '{}' (an object), and
/// System.Text.Json threw on the first such row — GET /api/projects/{key}/comments returned 500 for
/// every project with an older comment. Reading must never throw on a shape it did not write.
/// </summary>
public class JsonStringListTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"secret\"")]
    [InlineData("42")]
    [InlineData("{\"a\":[\"x\"]}")]
    [InlineData("not json at all")]
    public void Parse_NonArrayOrInvalidJson_ReturnsEmptyList(string json)
    {
        Assert.Empty(JsonStringList.Parse(json));
    }

    [Fact]
    public void Parse_Array_ReturnsStrings_SkippingNonStringElements()
    {
        var list = JsonStringList.Parse("[\"secret\",\"pii\",3,null,{\"x\":1}]");
        Assert.Equal(new[] { "secret", "pii" }, list);
    }

    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var original = new List<string> { "secret", "token" };
        Assert.Equal(original, JsonStringList.Parse(JsonStringList.Serialize(original)));
        Assert.Equal("[]", JsonStringList.Serialize(new List<string>()));
    }
}
