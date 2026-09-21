using FluentValidation;
using Pointer.Application.DTOs.Workspace;
using Pointer.Domain.Enums;

namespace Pointer.Application.Validators;

/// <summary>
/// Dashboard-facing field-level mirror of the A1 definition rules (R4-01). The service
/// (CommentFieldService.ValidateDefinitions) re-checks everything — this validator exists so the
/// dashboard gets per-field messages instead of one server-level string.
/// </summary>
public class UpdateCommentFieldDefinitionsValidator : AbstractValidator<UpdateCommentFieldDefinitionsRequest>
{
    public const int MaxDefinitions = 10;

    public static readonly string KeyPattern = "^[a-z][a-z0-9_]{1,31}$";
    public static readonly string LabelPattern = @"^[\p{L}\p{N}\p{M}&().,'’\-/ ]{1,40}$";
    public static readonly string HostPattern = @"^(\*\.)?[a-z0-9-]+(\.[a-z0-9-]+)*$";
    public static readonly string SuggestedToolPattern = "^[a-z0-9][a-z0-9-]*$";

    public UpdateCommentFieldDefinitionsValidator()
    {
        RuleFor(x => x.Fields)
            .Must(f => f.Count <= MaxDefinitions)
            .WithMessage($"A workspace allows at most {MaxDefinitions} comment fields.")
            .Must(f => f.Select(d => d.Key).Distinct().Count() == f.Count)
            .WithMessage("Comment field keys must be distinct.");

        RuleForEach(x => x.Fields).ChildRules(f =>
        {
            f.RuleFor(d => d.Key)
                .Matches(KeyPattern)
                .WithMessage("Key must be 2-32 characters: a lower-case letter, then lower-case letters, digits or underscores.");

            f.RuleFor(d => d.Label)
                .Matches(LabelPattern)
                .WithMessage("Label must be 1-40 characters on a single line (letters, digits and &().,'’-/ ).");

            f.RuleFor(d => d.Type)
                .IsInEnum();

            f.RuleFor(d => d.Options)
                .Must((d, o) => d.Type != CommentFieldType.Select || (o.Count >= 1 && o.Count <= 20))
                .WithMessage("A select field needs 1-20 options.")
                .Must(o => o.All(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length <= 40))
                .WithMessage("Each option must be 1-40 characters.")
                .Must(o => o.Distinct().Count() == o.Count)
                .WithMessage("Options must be distinct.")
                .Must((d, o) => d.Type == CommentFieldType.Select || o.Count == 0)
                .WithMessage("Options are only allowed on select fields.");

            f.RuleFor(d => d.AllowedHosts)
                .Must(h => h.Count <= 10)
                .WithMessage("At most 10 allowed hosts.")
                .Must(h => h.All(x => System.Text.RegularExpressions.Regex.IsMatch(x, HostPattern)))
                .WithMessage("Hosts must be lower-case like example.com or *.example.com (single-word hosts such as localhost are allowed).")
                .Must((d, h) => d.Type == CommentFieldType.Url || h.Count == 0)
                .WithMessage("Allowed hosts are only allowed on link fields.");

            f.RuleFor(d => d.SuggestedTool)
                .MaximumLength(40)
                .Matches(SuggestedToolPattern)
                .When(d => !string.IsNullOrWhiteSpace(d.SuggestedTool))
                .WithMessage("Suggested tool must be a short lower-case slug like 'atlassian'.");

            f.RuleFor(d => d.Hint)
                .MaximumLength(120);
        });
    }
}
