using FluentValidation;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreatePredefinedActionValidator : AbstractValidator<CreatePredefinedActionRequest>
{
    public CreatePredefinedActionValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.TextRequired)
            .MaximumLength(256);

        RuleFor(x => x.Prompt)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.PromptRequired);
    }
}

public class PredefinedActionInputValidator : AbstractValidator<PredefinedActionInput>
{
    public PredefinedActionInputValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.TextRequired)
            .MaximumLength(256);

        RuleFor(x => x.Prompt)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.PromptRequired);
    }
}
