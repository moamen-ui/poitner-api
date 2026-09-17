using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class CreateCommentValidator : AbstractValidator<CreateCommentRequest>
{
    public CreateCommentValidator()
    {
        RuleFor(x => x.Body)
            .NotEmpty().WithMessage(MessageKeys.Comment.BodyRequired)
            .MaximumLength(4000);

        RuleFor(x => x.Environment)
            .IsInEnum();

        // Client-detected BCP-47 primary tag (or the detector's "unknown" sentinel). The charset is
        // enforced here — not just the length — because the CLI prints this value into the AI apply
        // prompt OUTSIDE the untrusted fence; anything beyond [a-z0-9-] must never reach the DB.
        RuleFor(x => x.Language)
            .MaximumLength(16)
            .Matches("^(unknown|[a-zA-Z]{2,3}(-[a-zA-Z0-9]{1,8})*)$")
            .When(x => !string.IsNullOrWhiteSpace(x.Language))
            .WithMessage("Language must be a BCP-47 tag like 'en' or 'pt-br', or 'unknown'.");
    }
}
