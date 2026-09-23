using FluentValidation;
using Pointer.Application.DTOs.Auth;

namespace Pointer.Application.Validators;

public class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(254);

        RuleFor(x => x.Password)
            .NotEmpty();

        RuleFor(x => x.ProjectKey)
            .MaximumLength(64)
            .When(x => x.ProjectKey != null);
    }
}
