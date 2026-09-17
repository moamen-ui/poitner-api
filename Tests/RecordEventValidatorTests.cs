using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Event;
using Pointer.Application.Validators;
using Xunit;

public class RecordEventValidatorTests
{
    [Theory]
    [InlineData("cli")]
    [InlineData("web-component")]
    [InlineData(null)]
    [InlineData("")]
    public void Accepts_known_shaped_sources(string? source)
    {
        var r = new RecordEventValidator().TestValidate(new RecordEventRequest { Type = "widget_language", Source = source });
        r.ShouldNotHaveValidationErrorFor(x => x.Source);
    }

    // usage_events.source is varchar(32): an oversized or odd value must be a 400, never a DB exception.
    [Theory]
    [InlineData("this-source-name-is-longer-than-thirty-two")]
    [InlineData("Web Component")]
    [InlineData("-leading-dash")]
    public void Rejects_oversized_or_malformed_sources(string source)
    {
        var r = new RecordEventValidator().TestValidate(new RecordEventRequest { Type = "widget_language", Source = source });
        r.ShouldHaveValidationErrorFor(x => x.Source);
    }

    [Fact]
    public void Rejects_unknown_type()
    {
        var r = new RecordEventValidator().TestValidate(new RecordEventRequest { Type = "nope" });
        r.ShouldHaveValidationErrorFor(x => x.Type);
    }
}
