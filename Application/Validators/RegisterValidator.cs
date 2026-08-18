using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired)
            .MinimumLength(8).WithMessage(MessageKeys.User.PasswordWeak);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage(MessageKeys.User.DisplayNameRequired);

        RuleFor(x => x.RoleId)
            .GreaterThan(0).WithMessage(MessageKeys.Role.Invalid);

        // NOT a format/Matches check here: this is a REFERENCE to an existing project (resolved
        // case-insensitively — AuthService.RegisterAsync lowercases before matching), not a new key
        // being minted. Rejecting on case would break a legitimately-cased existing key.
        RuleFor(x => x.ProjectKey)
            .NotEmpty().WithMessage(MessageKeys.Project.KeyRequired);
    }
}
