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

        // usage_events.source is a real varchar(32); without this an oversized value is a 500, not a 400.
        RuleFor(x => x.Source)
            .MaximumLength(32)
            .Matches("^[a-z][a-z0-9-]*$")
            .When(x => !string.IsNullOrWhiteSpace(x.Source))
            .WithMessage("Invalid event source.");
    }
}
