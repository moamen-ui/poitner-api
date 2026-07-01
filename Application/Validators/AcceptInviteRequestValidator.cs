using FluentValidation;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

/// <summary>
/// M2: validates the anonymous invite-accept body. The auto-pipeline is NOT wired in Program.cs
/// (AddFluentValidationAutoValidation is absent), so the service guards nulls defensively before
/// reaching .Trim()/.Hash(). This validator is registered by AddValidatorsFromAssembly and can be
/// consumed explicitly if needed; the service-level guards are the primary enforcement.
/// </summary>
public class AcceptInviteRequestValidator : AbstractValidator<AcceptInviteRequest>
{
    public AcceptInviteRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty();

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage(MessageKeys.User.PasswordWeak);

        RuleFor(x => x.DisplayName)
            .NotEmpty();
    }
}
