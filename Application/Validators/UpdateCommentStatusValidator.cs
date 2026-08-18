using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdateCommentStatusValidator : AbstractValidator<UpdateCommentStatusRequest>
{
    public UpdateCommentStatusValidator()
    {
        RuleFor(x => x.Status).IsInEnum().WithMessage(MessageKeys.Comment.StatusInvalid);

        RuleFor(x => x.Reply).MaximumLength(4000);
        RuleFor(x => x.AppliedByLabel).MaximumLength(128);
    }
}
