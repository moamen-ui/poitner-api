using System.Reflection;
using Pointer.Application.DTOs.Comment;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING: the advisory payload flags must never reach an AI-facing payload.
///
/// The flag is text an attacker can influence the presence of. Inside an apply prompt it becomes
/// one more sentence the model reads — an injection surface created by a feature whose whole point
/// was to reduce risk. Two guarantees stack, and both are asserted here:
///
///   1. DTO SHAPE — the AI-facing DTOs do not define the fields at all, so no mapper can populate
///      them by accident.
///   2. CALLER GATING — the shared human DTOs define them as nullable and leave them null unless
///      the request came from the widget or dashboard. (Exercised through CommentService in
///      PayloadFlagGatingTests; here we pin the shape that makes the gate possible.)
/// </summary>
public class PayloadFlagExposureTests
{
    private static bool HasField(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    [Theory]
    [InlineData(typeof(CommentSummaryDto))]
    [InlineData(typeof(CommentApplyItemDto))]
    [InlineData(typeof(ApplyReplyDto))]
    public void AiFacingDto_DoesNotDefineTheFlagsAtAll(Type dto)
    {
        // Shape, not just value. A field that does not exist cannot be populated by a future mapper
        // that spreads an entity, which is how this kind of leak actually happens.
        Assert.False(HasField(dto, "HasPayloadFlag"), $"{dto.Name} must not expose HasPayloadFlag");
        Assert.False(HasField(dto, "PayloadFlags"), $"{dto.Name} must not expose PayloadFlags");
    }

    [Fact]
    public void ApplyQueue_DoesNotEmbedTheHumanReplyDto()
    {
        // CommentApplyItemDto used to embed List<ReplyResponse> — the same type the dashboard uses.
        // That coupling is what would carry any human-only field into the AI payload, silently,
        // the moment someone adds one.
        var replies = typeof(CommentApplyItemDto).GetProperty("Replies");
        Assert.NotNull(replies);

        var elementType = replies!.PropertyType.GetGenericArguments().Single();
        Assert.Equal(typeof(ApplyReplyDto), elementType);
        Assert.NotEqual(typeof(ReplyResponse), elementType);
    }

    [Fact]
    public void ApplyReplyDto_CarriesOnlyWhatAnApplyPromptNeeds()
    {
        // Deliberately minimal. Every field added here appears in an AI prompt, so the set is a
        // decision rather than whatever the entity happens to hold.
        var names = typeof(ApplyReplyDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "AuthorName", "Body", "CreatedAt" }, names);
    }

    [Theory]
    [InlineData(typeof(CommentResponse))]
    [InlineData(typeof(CommentListItemDto))]
    [InlineData(typeof(ReplyResponse))]
    public void HumanDto_DefinesTheFlagsAsNullable_SoTheyCanBeOmitted(Type dto)
    {
        // Nullable + JsonIgnore(WhenWritingNull) means a non-human caller gets the KEYS ABSENT,
        // not `false`/`[]`. Absent is the honest answer: the server is declining to say, rather
        // than asserting the text is clean.
        var hasFlag = dto.GetProperty("HasPayloadFlag");
        Assert.NotNull(hasFlag);
        Assert.Equal(typeof(bool?), hasFlag!.PropertyType);

        var flags = dto.GetProperty("PayloadFlags");
        Assert.NotNull(flags);
        Assert.Equal(typeof(List<string>), Nullable.GetUnderlyingType(flags!.PropertyType) ?? flags.PropertyType);

        foreach (var prop in new[] { hasFlag, flags })
        {
            var attr = prop.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>();
            Assert.True(
                attr?.Condition == System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                $"{dto.Name}.{prop.Name} must be omitted when null, not serialised as null");
        }
    }
}
