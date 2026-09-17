using FluentValidation;
using Pointer.Application.DTOs.Event;

namespace Pointer.Application.Validators;

public class RecordEventValidator : AbstractValidator<RecordEventRequest>
{
    public RecordEventValidator()
    {
        RuleFor(x => x.Type)
            .Must(t => t is "installed" or "doctor_run" or "apply_started" or "apply_failed" or "widget_language")
            .WithMessage("Invalid event type.");
    }
}
