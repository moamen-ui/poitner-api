using FluentValidation;
using Pointer.Application.DTOs.Role;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdateRoleValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage(MessageKeys.Role.NameRequired)
            .MaximumLength(64)
            .When(x => x.Name != null);
    }
}
