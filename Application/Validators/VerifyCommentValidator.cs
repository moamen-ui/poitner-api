using FluentValidation;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class VerifyCommentValidator : AbstractValidator<VerifyCommentRequest>
{
    public VerifyCommentValidator()
    {
        RuleFor(x => x.Note)
            .NotEmpty()
            .WithMessage(MessageKeys.Comment.VerifyNoteRequired)
            .When(x => !x.Ok);

        RuleFor(x => x.Note)
            .MaximumLength(500);
    }
}
