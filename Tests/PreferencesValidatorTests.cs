using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Validators;
using Xunit;

public class PreferencesValidatorTests
{
    [Fact]
    public void Rejects_bad_language()
    {
        new UpdatePreferencesValidator()
            .TestValidate(new UpdatePreferencesRequest { Language = "fr" })
            .ShouldHaveValidationErrorFor(x => x.Language);
    }

    [Fact]
    public void Accepts_valid_and_null()
    {
        new UpdatePreferencesValidator()
            .TestValidate(new UpdatePreferencesRequest { Language = "ar", Theme = null })
            .ShouldNotHaveAnyValidationErrors();
    }
}
