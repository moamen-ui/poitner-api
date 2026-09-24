using System.Net;

namespace Pointer.Application.Common.Email;

/// <summary>
/// The responsive 580px card shell every transactional e-mail renders into: outer full-width
/// background, centred white card with brand accent bar, header (logo or typographic wordmark),
/// content cell, and a footer naming the product.
/// </summary>
/// <remarks>
/// The shell is deliberately workspace-agnostic: several templates omit the workspace mention
/// entirely (a send with no resolvable workspace name), and their tests assert the word never
/// appears. Nothing in this layout — header, footer, meta tags, boilerplate — may mention it.
/// </remarks>
public static class EmailLayout
{
    public const string DefaultBrandColor = "#2563eb";

    /// <summary>Wraps <paramref name="contentHtml"/> (trusted, already-assembled body markup)
    /// into the full HTML document.</summary>
    public static string Wrap(
        string contentHtml,
        string productName,
        string? brandColor = null,
        string? logoUrl = null,
        string? appUrl = null,
        string? preheader = null,
        string? disclaimer = null,
        string lang = "en"
    )
    {
        var color = NormalizeBrandColor(brandColor);
        var safeProductName = Html(productName);
        var isAr = string.Equals(lang, "ar", StringComparison.OrdinalIgnoreCase);

        // dir="rtl" goes on the content/header/footer cells, NOT on <html>: on <html> it flips the outer
        // centering table (Chrome shifted the card off-screen) and Gmail strips <html> attributes anyway.
        var htmlTagAttrs = isAr ? "lang=\"ar\"" : "lang=\"en\"";
        var rtlCellAttr = isAr ? " dir=\"rtl\"" : string.Empty;
        var rtlAlignStyle = isAr ? "text-align:right;" : string.Empty;

        var headerHtml = !string.IsNullOrWhiteSpace(logoUrl)
            ? $"""
                <img src="{Html(
                    logoUrl
                )}" alt="{safeProductName}" style="display:block;height:34px;width:auto;border:0;outline:none;text-decoration:none;" />
                """
            : $"""
                <span style="font-size:18px;font-weight:700;letter-spacing:-0.02em;color:#0f172a;">{safeProductName}</span>
                """;

        var appLine = !string.IsNullOrWhiteSpace(appUrl)
            ? $" &middot; <a href=\"{Html(appUrl)}\" style=\"color:#64748b;text-decoration:none;\">{Html(appUrl)}</a>"
            : string.Empty;

        // The only fixed English string in the layout chrome itself (everything else — content,
        // disclaimer, preheader — is already localized by the caller before it reaches Wrap).
        var sentByLabel = isAr ? "أُرسلت بواسطة" : "Sent by";

        var disclaimerHtml = !string.IsNullOrWhiteSpace(disclaimer)
            ? $"<p style=\"margin:0;\">{disclaimer}</p>"
            : string.Empty;

        var preheaderHtml = !string.IsNullOrWhiteSpace(preheader)
            ? $"""
                <div style="display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;">{Html(
                    preheader
                )}&nbsp;&zwnj;</div>
                """
            : string.Empty;

        return $$"""
            <!DOCTYPE html>
            <html {{htmlTagAttrs}}>
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <meta http-equiv="X-UA-Compatible" content="IE=edge" />
            <meta name="x-apple-disable-message-reformatting" />
            <meta name="color-scheme" content="light" />
            <meta name="supported-color-schemes" content="light" />
            <title>{{safeProductName}}</title>
            <!--[if mso]>
            <xml><o:OfficeDocumentSettings><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml>
            <![endif]-->
            <style>
              body, table, td, a { -webkit-text-size-adjust: 100%; -ms-text-size-adjust: 100%; }
              table, td { mso-table-lspace: 0pt; mso-table-rspace: 0pt; }
              img { -ms-interpolation-mode: bicubic; border: 0; height: auto; line-height: 100%; outline: none; text-decoration: none; }
              body { margin: 0; padding: 0; width: 100% !important; height: 100% !important; background-color: #f8fafc; }
              a { color: {{color}}; }
              @media only screen and (max-width: 620px) {
                .email-card { width: 100% !important; border-radius: 0 !important; border-left: 0 !important; border-right: 0 !important; }
                .email-header { padding: 24px 20px 0 20px !important; }
                .email-content { padding: 24px 20px 28px 20px !important; }
                .email-footer { padding: 18px 20px !important; }
              }
            </style>
            </head>
            <body style="margin:0;padding:0;background-color:#f8fafc;">
            {{preheaderHtml}}
            <table role="presentation" width="100%" bgcolor="#f8fafc" cellpadding="0" cellspacing="0" border="0">
            <tr>
              <td align="center" style="padding:32px 16px;">
                <table role="presentation" class="email-card" cellpadding="0" cellspacing="0" border="0" style="max-width:580px;width:100%;background-color:#ffffff;border-radius:12px;border:1px solid #e2e8f0;overflow:hidden;box-shadow:0 4px 6px -1px rgba(0,0,0,0.05);">
                  <tr>
                    <td style="height:4px;line-height:4px;font-size:0;background-color:{{color}};">&nbsp;</td>
                  </tr>
                  <tr>
                    <td class="email-header"{{rtlCellAttr}} style="padding:28px 32px 0 32px;{{rtlAlignStyle}}">
                      {{headerHtml}}
                    </td>
                  </tr>
                  <tr>
                    <td class="email-content"{{rtlCellAttr}} style="padding:24px 32px 32px 32px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.6;color:#334155;{{rtlAlignStyle}}">
                      {{contentHtml}}
                    </td>
                  </tr>
                  <tr>
                    <td class="email-footer"{{rtlCellAttr}} style="padding:20px 32px;background-color:#f8fafc;border-top:1px solid #e2e8f0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:12px;color:#64748b;line-height:1.5;{{rtlAlignStyle}}">
                      <p style="margin:0 0 6px;">{{sentByLabel}} <strong>{{safeProductName}}</strong>{{appLine}}</p>
                      {{disclaimerHtml}}
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
            </table>
            </body>
            </html>
            """;
    }

    /// <summary>HTML-encodes a value for safe interpolation into markup (never render user input raw).</summary>
    public static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>The brand colour, falling back to the default blue when the install has none.</summary>
    public static string NormalizeBrandColor(string? brandColor) =>
        string.IsNullOrWhiteSpace(brandColor) ? DefaultBrandColor : Html(brandColor.Trim());
}
