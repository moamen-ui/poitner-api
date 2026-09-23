using FluentValidation;
using Pointer.Application.DTOs.Auth;

namespace Pointer.Application.Validators;

public class SwitchWorkspaceValidator : AbstractValidator<SwitchWorkspaceRequest>
{
    public SwitchWorkspaceValidator()
    {
        RuleFor(x => x.WorkspaceId).NotEmpty();
    }
}
