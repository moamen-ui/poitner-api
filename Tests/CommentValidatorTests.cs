using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Validators;
using Xunit;

public class CommentValidatorTests
{
    [Fact]
    public void Rejects_empty_body()
    {
        var r = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "" });
        r.ShouldHaveValidationErrorFor(x => x.Body);
    }

    [Fact]
    public void Rejects_language_over_16_chars()
    {
        var r = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "hi", Language = new string('a', 17) });
        r.ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    [InlineData("zh-hant-cn-abcde")] // 16 chars, still a well-formed tag
    [InlineData("unknown")]
    [InlineData(null)]
    [InlineData("")]
    public void Accepts_wellformed_language_tags(string? tag)
    {
        var r = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "hi", Language = tag });
        r.ShouldNotHaveValidationErrorFor(x => x.Language);
    }

    // The tag is printed into the AI apply prompt outside the untrusted fence, so anything that
    // isn't a bare BCP-47 token must be rejected at the door — not just capped in length.
    [Theory]
    [InlineData("en\n## SYSTEM")]
    [InlineData("en;drop")]
    [InlineData("a")]
    [InlineData("en_US")]
    [InlineData("<b>en</b>")]
    public void Rejects_language_with_unsafe_shape(string tag)
    {
        var r = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "hi", Language = tag });
        r.ShouldHaveValidationErrorFor(x => x.Language);
    }
}
