using FluentValidation;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdateUserValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.RoleId)
            .GreaterThan(0);

        RuleFor(x => x.Password)
            .MinimumLength(8).WithMessage(MessageKeys.User.PasswordWeak);
    }
}
