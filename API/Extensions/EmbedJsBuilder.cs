using System.Linq;

namespace Pointer.API.Extensions;

/// <summary>
/// Builds the self-configuring script served at <c>/embed.js</c> (see Program.cs). Extracted to a
/// pure, static method so the environment-attribute decision is unit-testable without booting the
/// whole app.
///
/// <c>environment</c> is deliberately opt-in: per <c>pointer-init.md</c> ("An `environment`
/// attribute, if present, overrides that resolution"), the widget otherwise resolves its comment
/// environment per request from the page origin against the project's registered app URLs. Writing
/// an `environment` attribute by default (as this used to do, always defaulting to "staging") silently
/// disabled that resolution for every embed.js install. Only a caller that explicitly asked to pin
/// one — a valid <c>?environment=</c> query value — gets the attribute at all.
/// </summary>
public static class EmbedJsBuilder
{
    public static bool Safe(string s) =>
        s.Length > 0 && s.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');

    /// <param name="origin">This server's public origin (see <see cref="PointerUrlResolver"/>).</param>
    /// <param name="project">The already-validated project key (empty string when absent/invalid).</param>
    /// <param name="environment">
    /// The already-validated <c>?environment=</c> value, or <c>null</c> when none was explicitly
    /// requested (absent, empty, or failed <see cref="Safe"/>). Only a non-null value produces an
    /// `environment` attribute on the injected element.
    /// </param>
    public static string Build(string origin, string project, string? environment)
    {
        var environmentAttr = environment is null
            ? ""
            : $"\n    el.setAttribute('environment', '{environment}');";

        return $$"""
(function () {
  if (window.__pointerEmbedded) return;
  window.__pointerEmbedded = true;
  var server = '{{origin}}';
  function mount() {
    var s = document.createElement('script');
    s.src = server + '/widget.js';
    s.defer = true;
    document.head.appendChild(s);
    var el = document.createElement('pointer-feedback');
    el.setAttribute('project', '{{project}}');
    el.setAttribute('server', server);{{environmentAttr}}
    document.body.appendChild(el);
  }
  // embed.js may run in <head> before <body> exists — wait for the DOM.
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
  else mount();
})();
""";
    }
}
