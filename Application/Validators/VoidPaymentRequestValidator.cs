using FluentValidation;
using Pointer.Application.DTOs.Billing;

namespace Pointer.Application.Validators;

public class VoidPaymentRequestValidator : AbstractValidator<VoidPaymentRequest>
{
    public VoidPaymentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
