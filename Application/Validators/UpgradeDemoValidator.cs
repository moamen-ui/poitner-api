using FluentValidation;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpgradeDemoValidator : AbstractValidator<UpgradeDemoRequest>
{
    public UpgradeDemoValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage(MessageKeys.User.PasswordWeak);
    }
}
