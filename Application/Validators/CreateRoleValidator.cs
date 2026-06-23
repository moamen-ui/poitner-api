using FluentValidation;
using Pointer.Application.DTOs.Role;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateRoleValidator : AbstractValidator<CreateRoleRequest>
{
    public CreateRoleValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage(MessageKeys.Role.NameRequired)
            .MaximumLength(64);
    }
}
