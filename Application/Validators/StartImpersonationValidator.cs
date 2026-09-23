using FluentValidation;
using Pointer.Application.DTOs.Impersonation;

namespace Pointer.Application.Validators;

public class StartImpersonationValidator : AbstractValidator<StartImpersonationRequest>
{
    public StartImpersonationValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().Length(10, 500);
        RuleFor(x => x.Minutes).InclusiveBetween(1, 60);
    }
}
