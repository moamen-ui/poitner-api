using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class ChangeEmailRequestValidator : AbstractValidator<ChangeEmailRequest>
{
    public ChangeEmailRequestValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired);

        RuleFor(x => x.NewEmail)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress().WithMessage(MessageKeys.User.EmailRequired)
            .MaximumLength(256).WithMessage(MessageKeys.User.EmailRequired);
    }
}
