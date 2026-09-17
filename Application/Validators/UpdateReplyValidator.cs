using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdateReplyValidator : AbstractValidator<UpdateReplyRequest>
{
    public UpdateReplyValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage(MessageKeys.Comment.BodyRequired)
            .MaximumLength(4000);
    }
}
