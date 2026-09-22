using FluentValidation;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

/// <summary>
/// Dashboard-facing field-level mirror of WorkspaceService.RenameAsync's rules (DB-03b). The
/// service re-checks everything — this validator exists so the dashboard gets a per-field message
/// instead of one server-level string.
/// </summary>
public class UpdateWorkspaceNameValidator : AbstractValidator<UpdateWorkspaceNameRequest>
{
    public const int MaxNameLength = 120;

    public UpdateWorkspaceNameValidator()
    {
        RuleFor(x => (x.Name ?? string.Empty).Trim())
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage(MessageKeys.Workspace.NameRequired)
            .MaximumLength(MaxNameLength)
            .WithMessage(MessageKeys.Workspace.NameTooLong)
            .Must(name => !name.Any(char.IsControl))
            .WithMessage(MessageKeys.Workspace.NameInvalid);
    }
}
