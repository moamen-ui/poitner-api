using FluentValidation;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

/// <summary>DB-20 §3.6e. Review finding #4: mirrors the shape TenantService.ChangePlanAsync
/// already enforces defensively (comp_reason varchar(200); comp_ends_at must be in the future when
/// set) so a bad request is a 400 before it ever reaches the service/DB.</summary>
public class ChangeTenantPlanRequestValidator : AbstractValidator<ChangeTenantPlanRequest>
{
    public ChangeTenantPlanRequestValidator()
    {
        RuleFor(x => x.CompReason)
            .MaximumLength(200)
            .WithMessage(MessageKeys.Billing.CompReasonTooLong);
        RuleFor(x => x.CompEndsAt)
            .GreaterThan(_ => DateTime.UtcNow)
            .When(x => x.CompEndsAt.HasValue)
            .WithMessage(MessageKeys.Billing.CompEndsAtMustBeFuture);
    }
}
