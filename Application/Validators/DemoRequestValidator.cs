using FluentValidation;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class DemoRequestValidator : AbstractValidator<DemoRequest>
{
    public DemoRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage(MessageKeys.User.EmailRequired)
            .EmailAddress();
    }
}
