using System.Text.RegularExpressions;
using FluentValidation;
using Pointer.Application.DTOs.DiscountCode;
using Pointer.Application.Resources;
using Pointer.Domain.Enums;

namespace Pointer.Application.Validators;

/// <summary>DB-20 §3.4. Mirrors the DB check constraints (ck_discount_codes_code_format,
/// ck_discount_codes_value, ck_discount_codes_window, ck_discount_codes_max_redemptions) so a bad
/// request is a 400 before it ever reaches Postgres. The service still normalises
/// (<c>Trim().ToUpperInvariant()</c>) before storing — this validator checks the normalised shape.</summary>
public class CreateDiscountCodeRequestValidator : AbstractValidator<CreateDiscountCodeRequest>
{
    public CreateDiscountCodeRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .Must(c => Regex.IsMatch(c.Trim().ToUpperInvariant(), "^[A-Z0-9][A-Z0-9_-]{2,31}$"))
            .WithMessage(MessageKeys.DiscountCode.CodeFormat);
        RuleFor(x => x.Label).MaximumLength(120);
        RuleFor(x => x.Note).MaximumLength(500);
        RuleFor(x => x.Duration).IsInEnum();
        RuleFor(x => x.MaxRedemptions)
            .GreaterThan(0)
            .When(x => x.MaxRedemptions.HasValue)
            .WithMessage(MessageKeys.DiscountCode.InvalidValue);
        RuleFor(x => x)
            .Must(x => x.ValidFrom == null || x.ValidUntil == null || x.ValidUntil > x.ValidFrom)
            .WithMessage(MessageKeys.DiscountCode.InvalidWindow);
        RuleFor(x => x)
            .Must(x => DiscountValueShapeValid(x.Kind, x.Value, x.Currency))
            .WithMessage(MessageKeys.DiscountCode.InvalidValue);
    }

    internal static bool DiscountValueShapeValid(DiscountKind kind, decimal value, string? currency) =>
        kind == DiscountKind.Percent
            ? value > 0 && value <= 100 && currency == null
            : kind == DiscountKind.FixedAmount
                && value > 0
                && currency != null
                && Regex.IsMatch(currency.Trim().ToUpperInvariant(), "^[A-Z]{3}$");
}

public class UpdateDiscountCodeRequestValidator : AbstractValidator<UpdateDiscountCodeRequest>
{
    public UpdateDiscountCodeRequestValidator()
    {
        RuleFor(x => x.Label).MaximumLength(120);
        RuleFor(x => x.Note).MaximumLength(500);
        RuleFor(x => x.Duration).IsInEnum();
        RuleFor(x => x.MaxRedemptions)
            .GreaterThan(0)
            .When(x => x.MaxRedemptions.HasValue)
            .WithMessage(MessageKeys.DiscountCode.InvalidValue);
        RuleFor(x => x)
            .Must(x => x.ValidFrom == null || x.ValidUntil == null || x.ValidUntil > x.ValidFrom)
            .WithMessage(MessageKeys.DiscountCode.InvalidWindow);
        RuleFor(x => x)
            .Must(x =>
                CreateDiscountCodeRequestValidator.DiscountValueShapeValid(x.Kind, x.Value, x.Currency)
            )
            .WithMessage(MessageKeys.DiscountCode.InvalidValue);
    }
}
