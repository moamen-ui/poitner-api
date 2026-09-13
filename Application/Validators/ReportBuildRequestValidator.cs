using FluentValidation;
using Pointer.Application.DTOs.Build;

namespace Pointer.Application.Validators;

/// <summary>
/// Shape-checks a build report before it reaches the service.
///
/// The service normalises and re-validates the same values — this is not redundant. The validator
/// turns a malformed body into a 400 with field-level detail, which is what a caller needs to fix
/// their call; the service's own check is what guarantees the invariant regardless of who invoked
/// it (a test, another service, a future endpoint).
/// </summary>
public class ReportBuildRequestValidator : AbstractValidator<ReportBuildRequest>
{
    private const string ShaPattern = "^[0-9a-fA-F]{7,40}$";

    public ReportBuildRequestValidator()
    {
        RuleFor(x => x.Sha)
            .NotEmpty()
            .Must(sha => System.Text.RegularExpressions.Regex.IsMatch(sha?.Trim() ?? string.Empty, ShaPattern))
            .WithMessage("Sha must be 7-40 hexadecimal characters.");

        // Bounded: this list drives an IN clause, and an unbounded one is a cheap way to make the
        // database do arbitrary work. 200 is far more than any real deploy carries.
        RuleFor(x => x.ContainsCommitShas!)
            .Must(list => list.Count <= 200)
            .When(x => x.ContainsCommitShas != null)
            .WithMessage("Send at most 200 commit shas in one report.");

        RuleForEach(x => x.ContainsCommitShas!)
            .Must(sha => System.Text.RegularExpressions.Regex.IsMatch(sha?.Trim() ?? string.Empty, ShaPattern))
            .When(x => x.ContainsCommitShas != null)
            .WithMessage("Each commit sha must be 7-40 hexadecimal characters.");
    }
}
