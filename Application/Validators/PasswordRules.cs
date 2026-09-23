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
        rule.NotEmpty()
            .WithMessage(MessageKeys.User.PasswordRequired)
            .Custom(
                (pw, ctx) =>
                {
                    if (PasswordPolicy.Validate(pw, email(ctx.InstanceToValidate)) is string err)
                        ctx.AddFailure(err);
                }
            );
}
