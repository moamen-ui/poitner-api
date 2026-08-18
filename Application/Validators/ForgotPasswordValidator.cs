using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class ForgotPasswordValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress();
    }
}
