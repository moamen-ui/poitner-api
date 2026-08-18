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
            .Matches("^[a-z0-9._-]+$").WithMessage(MessageKeys.Project.KeyInvalidFormat);

        RuleFor(x => x.Name)
            .NotEmpty();
    }
}
