using FluentValidation;
using Pointer.Application.DTOs.Project;

namespace Pointer.Application.Validators;

public class UpdateProjectValidator : AbstractValidator<UpdateProjectRequest>
{
    public UpdateProjectValidator()
    {
        // Key is immutable (not part of this DTO) — only Name can be blanked out if unguarded.
        RuleFor(x => x.Name)
            .NotEmpty()
            .When(x => x.Name != null);
    }
}
