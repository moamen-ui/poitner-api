using FluentValidation;
using Pointer.Application.DTOs.Workspace;

namespace Pointer.Application.Validators;

/// <summary>DB-18 §3.4. Anonymous preview/pause-instead — Token required.</summary>
public class WorkspaceDeletionTokenRequestValidator
    : AbstractValidator<WorkspaceDeletionTokenRequest>
{
    public WorkspaceDeletionTokenRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}
