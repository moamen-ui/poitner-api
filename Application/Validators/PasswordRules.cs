using FluentValidation;
using Pointer.Application.Common;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

/// <summary>
/// DB-14 §3.6. Applies <see cref="PasswordPolicy.Validate"/> as one FluentValidation rule — 10–128
/// chars, not in the embedded top-1000 list, not the e-mail (or its local part). <paramref name="email"/>
/// resolves the request's own e-mail field (or null when the request has none, e.g. change-password/
/// reset-password, which never carry an address to compare against).
/// </summary>
public static class PasswordRules
{
    public static IRuleBuilderOptionsConditions<T, string> StrongPassword<T>(
        this IRuleBuilder<T, string> rule,
        Func<T, string?> email
    ) =>
        // Review finding #6 (nit): early-return on an empty password inside the single Custom rule
        // so it yields exactly one failure. The previous NotEmpty().WithMessage().Custom() chain ran
        // BOTH rules for an empty string — NotEmpty's PasswordRequired AND PasswordPolicy.Validate's
        // MinLength check (PasswordWeak) — because IRuleBuilder<T, string> (as opposed to
        // IRuleBuilderInitial<T, TProperty>, obtained only from RuleFor) has no Cascade(Stop) to stop
        // that. Folding both checks into one Custom rule is the equivalent single-failure behavior.
        rule.Custom(
            (pw, ctx) =>
            {
                if (string.IsNullOrEmpty(pw))
                {
                    ctx.AddFailure(MessageKeys.User.PasswordRequired);
                    return;
                }
                if (PasswordPolicy.Validate(pw, email(ctx.InstanceToValidate)) is string err)
                    ctx.AddFailure(err);
            }
        );
}
