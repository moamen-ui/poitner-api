using FluentValidation;
using Pointer.Application.DTOs.Invite;

namespace Pointer.Application.Validators;

/// <summary>
/// M2: validates the admin invite-create body. All fields are optional but bounded when present.
/// Registered by AddValidatorsFromAssembly; the service applies sane defaults for missing values.
/// </summary>
public class CreateInviteRequestValidator : AbstractValidator<CreateInviteRequest>
{
    public CreateInviteRequestValidator()
    {
        // Email is optional; when present it must be a valid format (it becomes the email-lock).
        RuleFor(x => x.Email)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Email));

        // TTL bounds: 1..365 days when supplied.
        RuleFor(x => x.ExpiresInDays)
            .InclusiveBetween(1, 365)
            .When(x => x.ExpiresInDays.HasValue);

        // MaxUses must be at least 1 when set (null = unlimited).
        RuleFor(x => x.MaxUses)
            .GreaterThanOrEqualTo(1)
            .When(x => x.MaxUses.HasValue);
    }
}
