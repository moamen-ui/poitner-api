using FluentValidation;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Resources;

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

        // DB-20 §3.6e / review finding #4 — same comp-field shape as ChangeTenantPlanRequestValidator.
        RuleFor(x => x.CompReason)
            .MaximumLength(200)
            .WithMessage(MessageKeys.Billing.CompReasonTooLong);
        RuleFor(x => x.CompEndsAt)
            .GreaterThan(_ => DateTime.UtcNow)
            .When(x => x.CompEndsAt.HasValue)
            .WithMessage(MessageKeys.Billing.CompEndsAtMustBeFuture);
    }
}
