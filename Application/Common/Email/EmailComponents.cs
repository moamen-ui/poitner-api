using System.Net;
using System.Text;

namespace Pointer.Application.Common.Email;

/// <summary>
/// The reusable building blocks templates compose their bodies from. Every method returns a
/// self-contained markup fragment safe to drop into <see cref="EmailLayout.Wrap"/>'s content cell.
/// </summary>
public static class EmailComponents
{
    /// <summary>
    /// Table-based centred primary action button (bulletproof for clients that strip border-radius
    /// on anchors). The URL is attribute-encoded; token-bearing links pass through unchanged since
    /// Uri.EscapeDataString output contains no HTML-significant characters.
    /// </summary>
    public static string Button(string text, string url, string brandColor, bool rtl = false)
    {
        var color = EmailLayout.NormalizeBrandColor(brandColor);
        return $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:20px 0 24px;">
              <tr>
                <td style="background-color:{color};border-radius:8px;">
                  <a href="{EmailLayout.Html(
                url
            )}" target="_blank" style="display:inline-block;padding:12px 24px;font-size:14px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:8px;">{EmailLayout.Html(
                text
            )} {(rtl ? "&larr;" : "&rarr;")}</a>
                </td>
              </tr>
            </table>
            """;
    }

    /// <summary>
    /// A bordered highlight box for secondary detail or warnings. <paramref name="htmlContent"/>
    /// is trusted markup — encode user input before passing it in.
    /// </summary>
    public static string Callout(string htmlContent, string? borderColor = null, bool rtl = false)
    {
        var edge = string.IsNullOrWhiteSpace(borderColor) ? "#cbd5e1" : borderColor.Trim();
        return $"<div style=\"background-color:#f8fafc;border:1px solid #e2e8f0;{(rtl ? "border-right" : "border-left")}:4px solid {edge};border-radius:6px;padding:12px 16px;margin:16px 0;font-size:14px;color:#334155;line-height:1.5;\">{htmlContent}</div>";
    }

    /// <summary>
    /// A label/value table for credentials (demo logins, project keys). Values render as
    /// monospace chips when <c>IsCode</c> is set; both label and value are HTML-encoded here.
    /// </summary>
    public static string KeyValueTable(IEnumerable<(string Label, string Value, bool IsCode)> items)
    {
        var rows = new StringBuilder();
        foreach (var (label, value, isCode) in items)
        {
            var rendered = isCode
                ? $"<code style=\"font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;font-size:13px;background-color:#eef2ff;border:1px solid #e2e8f0;border-radius:4px;padding:2px 6px;color:#0f172a;word-break:break-all;\">{EmailLayout.Html(value)}</code>"
                : EmailLayout.Html(value);
            rows.Append(
                $"<tr><td style=\"padding:6px 14px 6px 0;color:#64748b;white-space:nowrap;\">{EmailLayout.Html(label)}</td><td style=\"padding:6px 0;\">{rendered}</td></tr>"
            );
        }

        return $"""
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;background-color:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;margin:16px 0;padding:4px 16px;font-size:14px;color:#334155;">
              <tbody>
                {rows}
              </tbody>
            </table>
            """;
    }

    /// <summary>A dark slate code block. The snippet is HTML-encoded so tags display as text.</summary>
    public static string CodeBlock(string code)
    {
        var encoded = WebUtility.HtmlEncode(code);
        return $"<pre style=\"background-color:#0f172a;border-radius:8px;padding:14px 16px;margin:12px 0 16px 0;font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;font-size:12px;line-height:1.5;color:#f8fafc;word-break:break-all;white-space:pre-wrap;\">{encoded}</pre>";
    }
}
