using FluentValidation;
using Pointer.Application.Common;
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

        RuleFor(x => x.AiTool)
            .MaximumLength(64)
            .Matches(AiAttributionPattern.Regex).When(x => !string.IsNullOrEmpty(x.AiTool))
            .WithMessage(MessageKeys.Comment.AiAttributionInvalid);

        RuleFor(x => x.AiModel)
            .MaximumLength(64)
            .Matches(AiAttributionPattern.Regex).When(x => !string.IsNullOrEmpty(x.AiModel))
            .WithMessage(MessageKeys.Comment.AiAttributionInvalid);
    }
}
