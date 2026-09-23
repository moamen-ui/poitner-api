using FluentValidation;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class UpgradeDemoValidator : AbstractValidator<UpgradeDemoRequest>
{
    public UpgradeDemoValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();

        RuleFor(x => x.Password).StrongPassword(x => x.Email);

        // DB-17 §3.4: same rule as DB-03b's rename — 120 chars, not whitespace-only when provided.
        // Null/blank is valid (falls back to Workspace.PlaceholderName in the service).
        When(
            x => !string.IsNullOrEmpty(x.WorkspaceName),
            () =>
            {
                RuleFor(x => x.WorkspaceName!.Trim())
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .WithMessage(MessageKeys.Workspace.NameRequired)
                    .MaximumLength(120)
                    .WithMessage(MessageKeys.Workspace.NameTooLong)
                    .Must(name => !name.Any(char.IsControl))
                    .WithMessage(MessageKeys.Workspace.NameInvalid);
            }
        );
    }
}
