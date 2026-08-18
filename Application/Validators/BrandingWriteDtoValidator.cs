using FluentValidation;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Resources;

namespace Pointer.Application.Validators;

public class BrandingWriteDtoValidator : AbstractValidator<BrandingWriteDto>
{
    public BrandingWriteDtoValidator()
    {
        RuleFor(x => x.ProductName).MaximumLength(64);
        RuleFor(x => x.Tagline).MaximumLength(160);

        RuleFor(x => x.PrimaryColor)
            .Matches("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$").WithMessage(MessageKeys.Branding.PrimaryColorInvalidFormat)
            .When(x => !string.IsNullOrWhiteSpace(x.PrimaryColor));

        RuleFor(x => x.Urls).SetValidator(new BrandingUrlsWriteDtoValidator()!);
    }
}

public class BrandingUrlsWriteDtoValidator : AbstractValidator<BrandingUrlsWriteDto>
{
    public BrandingUrlsWriteDtoValidator()
    {
        RuleFor(x => x.App).Must(BeHttpUrl).WithMessage(MessageKeys.Branding.UrlAppInvalidFormat)
            .When(x => !string.IsNullOrWhiteSpace(x.App));
        RuleFor(x => x.Demo).Must(BeHttpUrl).WithMessage(MessageKeys.Branding.UrlDemoInvalidFormat)
            .When(x => !string.IsNullOrWhiteSpace(x.Demo));
        RuleFor(x => x.Docs).Must(BeHttpUrl).WithMessage(MessageKeys.Branding.UrlDocsInvalidFormat)
            .When(x => !string.IsNullOrWhiteSpace(x.Docs));
        RuleFor(x => x.Landing).Must(BeHttpUrl).WithMessage(MessageKeys.Branding.UrlLandingInvalidFormat)
            .When(x => !string.IsNullOrWhiteSpace(x.Landing));
    }

    private static bool BeHttpUrl(string? url) =>
        url != null
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
