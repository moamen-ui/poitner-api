using FluentValidation;
using Pointer.Application.Common;
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
