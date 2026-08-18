using FluentValidation;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdatePredefinedActionValidator : AbstractValidator<UpdatePredefinedActionRequest>
{
    public UpdatePredefinedActionValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.TextRequired)
            .MaximumLength(256)
            .When(x => x.Text != null);

        RuleFor(x => x.Prompt)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.PromptRequired)
            .When(x => x.Prompt != null);

        RuleFor(x => x.SortOrder)
            .GreaterThanOrEqualTo(0);
    }
}
