using FluentValidation;
using Pointer.Application.DTOs.Tenant;

namespace Pointer.Application.Validators;

public class CreateTenantInviteValidator : AbstractValidator<CreateTenantInviteRequest>
{
    public CreateTenantInviteValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).MaximumLength(120);

        // The service clamps too — the legacy /api/admin/invites route reaches the same branch with
        // a 1–365 validator, so the bound cannot live here alone.
        RuleFor(x => x.ExpiresInDays).InclusiveBetween(1, 30).When(x => x.ExpiresInDays.HasValue);
    }
}
