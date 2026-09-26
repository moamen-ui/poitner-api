using FluentValidation;
using Pointer.Application.DTOs.Billing;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

/// <summary>DB-20 §3.6c step 4. The service re-validates <c>PaidAt</c>'s window (needs "now" at
/// call time) and defaults <c>Currency</c> from the quote — this only catches the cheap, static
/// shape errors before either of those run.</summary>
public class RecordPaymentRequestValidator : AbstractValidator<RecordPaymentRequest>
{
    public RecordPaymentRequestValidator()
    {
        RuleFor(x => x.Amount)
            .InclusiveBetween(0, 9_999_999_999.99m)
            .WithMessage(MessageKeys.Billing.InvalidAmount);
        RuleFor(x => x.Currency)
            .Matches("^[A-Za-z]{3}$")
            .When(x => !string.IsNullOrEmpty(x.Currency));
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Reference).MaximumLength(128);
        RuleFor(x => x.Note).MaximumLength(500);
        RuleFor(x => x.PaidAt).NotEqual(default(DateTime)).WithMessage(MessageKeys.Billing.InvalidPaidAt);
    }
}
