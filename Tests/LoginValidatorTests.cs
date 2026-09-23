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

    /// <summary>
    /// GLM review M1/F2 — an unbounded e-mail was the input that let a single anonymous IP grow
    /// the login-lockout cache without bound (one entry per distinct attacker-chosen e-mail, no
    /// MaximumLength). 254 is RFC 5321's practical e-mail length ceiling.
    /// </summary>
    [Fact]
    public void OverLongEmail_ShouldHaveValidationError()
    {
        // "a...a@example.com" - 255 chars total, one over the cap, still a syntactically valid
        // address so only MaximumLength (not EmailAddress) should be catching it.
        var localPart = new string('a', 255 - "@example.com".Length);
        var request = new LoginRequest { Email = $"{localPart}@example.com", Password = "x" };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void MaxLengthEmail_ShouldNotHaveLengthValidationError()
    {
        var localPart = new string('a', 254 - "@example.com".Length);
        var request = new LoginRequest
        {
            Email = $"{localPart}@example.com",
            Password = "x",
        };
        Assert.Equal(254, request.Email.Length);

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.Email);
    }

    /// <summary>DB-11b: ProjectKey is optional (widget-only, D11 auto-routing), but capped at 64
    /// chars when present — an unknown key is simply ignored, not a length attack surface.</summary>
    [Fact]
    public void ProjectKey_Null_ShouldNotHaveValidationError()
    {
        var request = new LoginRequest { Email = "a@b.com", Password = "x", ProjectKey = null };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.ProjectKey);
    }

    [Fact]
    public void ProjectKey_OverLength_ShouldHaveValidationError()
    {
        var request = new LoginRequest
        {
            Email = "a@b.com",
            Password = "x",
            ProjectKey = new string('k', 65),
        };
        var result = _validator.TestValidate(request);
        result.ShouldHaveValidationErrorFor(x => x.ProjectKey);
    }

    [Fact]
    public void ProjectKey_MaxLength_ShouldNotHaveValidationError()
    {
        var request = new LoginRequest
        {
            Email = "a@b.com",
            Password = "x",
            ProjectKey = new string('k', 64),
        };
        var result = _validator.TestValidate(request);
        result.ShouldNotHaveValidationErrorFor(x => x.ProjectKey);
    }
}
