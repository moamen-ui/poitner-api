using FluentValidation;
using Pointer.Application.DTOs.Workspace;

namespace Pointer.Application.Validators;

/// <summary>DB-18 §3.4. Anonymous confirm — a null/empty Token or WorkspaceName must 400, not 500;
/// Password is optional (passwordless identities confirm with the typed name only).</summary>
public class ConfirmWorkspaceDeletionRequestValidator
    : AbstractValidator<ConfirmWorkspaceDeletionRequest>
{
    public ConfirmWorkspaceDeletionRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.WorkspaceName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Password).MaximumLength(128);
    }
}
