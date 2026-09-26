using System.Reflection;
using FluentValidation;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Plan;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators.Plan;

/// <summary>
/// Auto-runs on binding. Validates the plan write payload: name/slug present + bounded, entitlement
/// values are known keys (via the catalog) and well-typed (ints allow -1 = unlimited).
/// </summary>
public class PlanWriteDtoValidator : AbstractValidator<PlanWriteDto>
{
    public PlanWriteDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage(MessageKeys.Plan.NameRequired)
            .MaximumLength(64);
        RuleFor(x => x.Slug)
            .NotEmpty()
            .WithMessage(MessageKeys.Plan.SlugRequired)
            .MaximumLength(64)
            .Matches("^[a-z0-9._-]+$");
        // DB-20 §5 task 18: tightened in code (R1 — the column itself stays varchar(8), no
        // narrowing) to ISO 4217 alpha-3, case-insensitive; PlanService stores it upper-cased.
        RuleFor(x => x.Currency).NotEmpty().Matches("^[A-Za-z]{3}$");
        RuleFor(x => x.PriceMonthly).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Interval).IsInEnum();
        RuleFor(x => x.DisplayState).IsInEnum();

        // Entitlements: every DTO property must be a KNOWN catalog key, and int values must be >= -1
        // (-1 = unlimited). The typed DTO already fixes the key set, but we assert it against the
        // catalog so a DTO/catalog drift is caught, and we type/range-check each supplied value.
        RuleFor(x => x.Entitlements)
            .Custom(
                (dto, ctx) =>
                {
                    if (dto == null)
                        return;
                    foreach (
                        var prop in typeof(PlanEntitlementsDto).GetProperties(
                            BindingFlags.Public | BindingFlags.Instance
                        )
                    )
                    {
                        if (!EntitlementCatalog.IsKnown(prop.Name))
                        {
                            ctx.AddFailure(
                                nameof(PlanWriteDto.Entitlements),
                                $"{MessageKeys.Plan.UnknownEntitlement} ({prop.Name})"
                            );
                            continue;
                        }

                        var value = prop.GetValue(dto);
                        if (value is int i && i < -1)
                            ctx.AddFailure(
                                nameof(PlanWriteDto.Entitlements),
                                $"{MessageKeys.Plan.InvalidEntitlementValue} ({prop.Name}={i}; -1 = unlimited, 0+ = a limit)"
                            );
                    }
                }
            );
    }
}
