using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class EditCommentValidator : AbstractValidator<EditCommentRequest>
{
    public EditCommentValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage(MessageKeys.Comment.BodyRequired)
            .MaximumLength(4000);
    }
}
