using FluentValidation;
using Pointer.Application.DTOs.User;

namespace Pointer.Application.Validators;

public class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8);

        RuleFor(x => x.DisplayName)
            .NotEmpty();

        RuleFor(x => x.RoleId)
            .GreaterThan(0);
    }
}
