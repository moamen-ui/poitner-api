using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired);

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired)
            .MinimumLength(8).WithMessage(MessageKeys.User.PasswordWeak);
    }
}
