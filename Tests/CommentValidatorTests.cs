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
}
