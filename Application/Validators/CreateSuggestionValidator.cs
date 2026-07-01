using FluentValidation;
using Pointer.Application.DTOs.Suggestion;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateSuggestionValidator : AbstractValidator<CreateSuggestionRequest>
{
    public CreateSuggestionValidator()
    {
        RuleFor(x => x.Text)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.TextRequired)
            .MaximumLength(256);

        RuleFor(x => x.Prompt)
            .NotEmpty().WithMessage(MessageKeys.PredefinedAction.PromptRequired)
            .MaximumLength(8000);
    }
}
