using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class AddReplyValidator : AbstractValidator<AddReplyRequest>
{
    public AddReplyValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage(MessageKeys.Comment.BodyRequired)
            .MaximumLength(4000);
    }
}
