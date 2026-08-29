using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Validators;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The project key contract: lowercase letters, digits and dashes only. CreateAsync is the only
/// path that mints a key — ProjectService.EnsureAsync is strict and never self-creates — so this
/// validator is the single gate, and the dashboards' auto-generated key must land inside it.
/// </summary>
public class CreateProjectKeyFormatTests
{
    private static CreateProjectRequest Req(string key) => new() { Key = key, Name = "A project" };

    private readonly CreateProjectValidator _validator = new();

    [Theory]
    [InlineData("my-app")]
    [InlineData("app")]
    [InlineData("a1")]
    [InlineData("pointer-dashboard")]
    [InlineData("moamen-new-project-name-sdf-fsdfsfsdf-dsgdfg")]
    [InlineData("mshrwa-jdyd")]              // an Arabic name, transliterated by the dashboard
    public void Accepts_lettersDigitsAndDashes(string key) =>
        _validator.TestValidate(Req(key)).ShouldNotHaveValidationErrorFor(x => x.Key);

    [Theory]
    [InlineData("web.app")]                  // dots no longer allowed
    [InlineData("my_app")]                   // underscores no longer allowed
    [InlineData("My-App")]                   // uppercase
    [InlineData("my app")]                   // whitespace
    [InlineData("my/app")]
    [InlineData("مشروع")]                    // must be transliterated before submitting
    [InlineData("")]
    public void Rejects_everythingElse(string key) =>
        _validator.TestValidate(Req(key)).ShouldHaveValidationErrorFor(x => x.Key);
}
