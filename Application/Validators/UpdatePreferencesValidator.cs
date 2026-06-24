using FluentValidation;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdatePreferencesValidator : AbstractValidator<UpdatePreferencesRequest>
{
    public UpdatePreferencesValidator()
    {
        RuleFor(x => x.Language)
            .Must(v => v is "ar" or "en")
            .When(x => x.Language != null)
            .WithMessage(MessageKeys.Preferences.Invalid);

        RuleFor(x => x.Theme)
            .Must(v => v is "light" or "dark")
            .When(x => x.Theme != null)
            .WithMessage(MessageKeys.Preferences.Invalid);
    }
}
