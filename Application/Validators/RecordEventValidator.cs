using FluentValidation;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Event;

namespace Pointer.Application.Validators;

public class RecordEventValidator : AbstractValidator<RecordEventRequest>
{
    public RecordEventValidator()
    {
        // DB-15: the accepted list lives in UsageEventTypes.ClientPostable — server-only funnel
        // types (demo_started, …) stay unpostable, so a client cannot forge a funnel step.
        RuleFor(x => x.Type)
            .Must(t => UsageEventTypes.ClientPostable.Contains(t))
            .WithMessage("Invalid event type.");

        // usage_events.source is a real varchar(32); without this an oversized value is a 500, not a 400.
        RuleFor(x => x.Source)
            .MaximumLength(32)
            .Matches("^[a-z][a-z0-9-]*$")
            .When(x => !string.IsNullOrWhiteSpace(x.Source))
            .WithMessage("Invalid event source.");
    }
}
