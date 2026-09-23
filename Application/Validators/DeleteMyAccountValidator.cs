using FluentValidation;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class DeleteMyAccountValidator : AbstractValidator<DeleteMyAccountRequest>
{
    public DeleteMyAccountValidator()
    {
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage(MessageKeys.User.PasswordRequired);
    }
}
