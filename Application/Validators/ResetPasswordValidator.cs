using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage(MessageKeys.Auth.TokenRequired);

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired)
            .MinimumLength(8).WithMessage(MessageKeys.User.PasswordWeak);
    }
}
