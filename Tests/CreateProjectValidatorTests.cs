using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Resources;
using Pointer.Application.Validators;
using Xunit;

public class CreateProjectValidatorTests
{
    [Fact]
    public void Rejects_empty_key()
    {
        var r = new CreateProjectValidator().TestValidate(new CreateProjectRequest { Key = "", Name = "N" });
        r.ShouldHaveValidationErrorFor(x => x.Key).WithErrorMessage(MessageKeys.Project.KeyRequired);
    }

    [Theory]
    [InlineData("My Project")] // space
    [InlineData("Bad-Key!")] // uppercase + special char
    [InlineData("has space")]
    public void Rejects_badly_formatted_key(string key)
    {
        var r = new CreateProjectValidator().TestValidate(new CreateProjectRequest { Key = key, Name = "N" });
        r.ShouldHaveValidationErrorFor(x => x.Key).WithErrorMessage(MessageKeys.Project.KeyInvalidFormat);
    }

    [Theory]
    [InlineData("my-project")]
    [InlineData("my_project.v2")]
    [InlineData("lms")]
    public void Accepts_wellformed_key(string key)
    {
        var r = new CreateProjectValidator().TestValidate(new CreateProjectRequest { Key = key, Name = "N" });
        r.ShouldNotHaveValidationErrorFor(x => x.Key);
    }
}
