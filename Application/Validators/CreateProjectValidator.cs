using FluentValidation;
using Pointer.Application.DTOs.Project;

namespace Pointer.Application.Validators;

public class CreateProjectValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty()
            .Matches("^[a-z0-9._-]+$");

        RuleFor(x => x.Name)
            .NotEmpty();
    }
}
