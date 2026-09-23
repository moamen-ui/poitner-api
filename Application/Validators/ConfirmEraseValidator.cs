using FluentValidation;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class ConfirmEraseValidator : AbstractValidator<ConfirmEraseRequest>
{
    public ConfirmEraseValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty().WithMessage(MessageKeys.Auth.TokenRequired);
    }
}
