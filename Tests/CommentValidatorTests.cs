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

    [Fact]
    public void Accepts_language_at_16_chars_or_null()
    {
        var r = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "hi", Language = new string('a', 16) });
        r.ShouldNotHaveValidationErrorFor(x => x.Language);

        var r2 = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "hi", Language = null });
        r2.ShouldNotHaveValidationErrorFor(x => x.Language);
    }
}
