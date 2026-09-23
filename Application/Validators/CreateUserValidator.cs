using FluentValidation;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress();

        RuleFor(x => x.Password)
            .StrongPassword(x => x.Email);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(MessageKeys.User.DisplayNameRequired);

        // A super admin targets an existing workspace instead of picking a role (the server forces
        // Deputy regardless) — RoleId is only meaningful, and only required, for a non-super-admin
        // caller adding to their own tenant. See UserService.CreateAsync's super-admin branch.
        RuleFor(x => x.RoleId)
            .GreaterThan(0).WithMessage(MessageKeys.Role.Invalid)
            .When(x => x.TargetOwnerId == null);
    }
}
