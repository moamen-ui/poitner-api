using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Validators;
using Xunit;

public class LoginValidatorTests
{
    private readonly LoginValidator _validator = new();

    [Fact]
    public void EmptyEmailAndPassword_ShouldHaveValidationErrors()
    {
        var request = new LoginRequest { Email = string.Empty, Password = string.Empty };
        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }
}
