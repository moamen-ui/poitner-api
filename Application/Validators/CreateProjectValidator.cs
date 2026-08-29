using FluentValidation;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateProjectValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage(MessageKeys.Project.KeyRequired)
            // Letters, digits and dashes only. EnsureAsync never self-creates, so this is the
            // single gate every project key passes through.
            .Matches("^[a-z0-9-]+$").WithMessage(MessageKeys.Project.KeyInvalidFormat);

        RuleFor(x => x.Name)
            .NotEmpty();
    }
}
