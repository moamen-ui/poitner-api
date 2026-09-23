using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class RegisterAdminValidator : AbstractValidator<RegisterAdminRequest>
{
    public RegisterAdminValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress();

        RuleFor(x => x.Password)
            .StrongPassword(x => x.Email);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(MessageKeys.User.DisplayNameRequired);
    }
}
