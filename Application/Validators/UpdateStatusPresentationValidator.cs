using FluentValidation;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpdateStatusPresentationValidator : AbstractValidator<UpdateStatusPresentationRequest>
{
    public UpdateStatusPresentationValidator()
    {
        RuleFor(x => x.Label)
            .NotEmpty().WithMessage(MessageKeys.Status.LabelRequired)
            .MaximumLength(64)
            .When(x => x.Label != null);

        RuleFor(x => x.Color)
            .Matches("^#[0-9a-fA-F]{6}$").WithMessage(MessageKeys.Status.ColorInvalidFormat);

        RuleFor(x => x.Order)
            .GreaterThanOrEqualTo(0).WithMessage(MessageKeys.Status.OrderInvalid);
    }
}
