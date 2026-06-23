using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateCommentValidator : AbstractValidator<CreateCommentRequest>
{
    public CreateCommentValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage(MessageKeys.Comment.BodyRequired)
            .MaximumLength(4000);

        RuleFor(x => x.Environment)
            .IsInEnum();
    }
}
