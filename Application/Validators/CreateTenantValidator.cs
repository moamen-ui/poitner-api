using FluentValidation;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateTenantValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired)
            .MinimumLength(8).WithMessage(MessageKeys.User.PasswordWeak);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(MessageKeys.User.DisplayNameRequired);
    }
}
