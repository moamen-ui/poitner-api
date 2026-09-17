using System.Text.RegularExpressions;

public class OnDiskContractTests
{
    private const string ContractDoc = "docs/ON-DISK-CONTRACT.md";

    // Every customer-visible name in docs/ON-DISK-CONTRACT.md. Keep in sync with the doc — the
    // same PR must update both, or FrozenNames_AreDocumented fails.
    private static readonly string[] FrozenNames =
    {
        // Directory + files written into it
        ".pointer/",
        "config.json",
        "credentials.env",
        "credentials.env.example",
        "stack.json",
        "pointer.sh",
        "manifest.json",
        ".token_cache",
        // config.json keys
        "server",
        "project",
        "environment",
        "aiTool",
        "skillsDir",
        "cliVersion",
        // .gitignore lines
        "!.pointer/credentials.env.example",
        "!.pointer/stack.json",
        "!.pointer/pointer.sh",
        "!.pointer/config.json",
        // Env vars + framework prefixes
        "POINTER_API_KEY",
        "POINTER_SERVER",
        "POINTER_PROJECT",
        "POINTER_ENV",
        "POINTER_ENABLED",
        "VITE_",
        "NEXT_PUBLIC_",
        "REACT_APP_",
        // Custom element + its attributes
        "<pointer-feedback>",
        "fixed-environment",
        "screenshot",
        "source-attr",
        // Window globals
        "window.__pointerEmbedded",
        "window.__POINTER_CONFIG__",
        "window.__POINTER_FETCH__",
        // Host-facing DOM attributes (brand-neutral by design)
        "data-component-source",
        "data-build-sha",
        "data-snapshot-mask",
        // Widget-set (output) attribute on <pointer-feedback> itself — see the ON-DISK-CONTRACT.md
        // footnote for why this is frozen, not internal, despite being widget-authored.
        "data-fbk-theme",
        // Served URLs
        "/widget.js",
        "/widget.css",
        "/pointer.js",
        "/pointer.css",
        "/embed.js",
        "/install.sh",
        "/skill.md",
        "/skills/apply.md",
        "/skills/translate.md",
        "/skills/advanced.md",
        "/pointer-init.md",
        "/pointer.sh",
        "/vendor/snapdom.js",
        // Skill directories
        ".claude/skills/pointer-init/",
        ".claude/skills/pointer-feedback/",
        ".agents/pointer-init/",
        ".agents/pointer-feedback/",
        // Browser storage keys ('pointer_env_' is the prefix of the dynamic key)
        "pointer_token",
        "pointer_user",
        "pointer_env_<project>",
        "pointer_toolbar_pos",
        "pointer_widget_theme",
        "pointer_widget_language",
        "pointer_visible",
        "pointer_page_session_id",
        "pointer_lang_reported",
        // MCP config + npm
        ".mcp.json",
        "pointer-feedback",
        "pointer",
    };

    // The three host-facing DOM attributes, frozen in the contract table.
    private static readonly string[] FrozenDomAttributes =
    {
        "data-component-source",
        "data-build-sha",
        "data-snapshot-mask",
        "data-fbk-theme",
    };

    // Non-customer-facing data-* attributes (widget shadow DOM, served docs, marketing markup).
    // Also listed as a footnote in docs/ON-DISK-CONTRACT.md.
    private static readonly string[] InternalAttributes =
    {
        "data-id",
        "data-act",
        // Reply edit/delete action wiring (comment card) — internal selectors, not host-facing.
        "data-comment-id",
        "data-reply-id",
        "data-toggle",
        "data-placement",
        "data-private",
        "data-c",
        "data-i",
        "data-path",
        "data-testid",
        "data-theme",
        "data-step",
        "data-brand-logo",
        "data-brand-name",
        // Toolbar position, moved off an inline style attribute so the widget runs under a host
        // page's strict Content-Security-Policy. Shadow-DOM internal; no host reads them.
        "data-fbk-left",
        "data-fbk-top",
        // Toolbar action/state hooks — internal selectors for element.ts's own click wiring, not a
        // host-facing API.
        "data-fbk-act",
        "data-fbk-drag",
        "data-fbk-count",
        "data-fbk-unread",
        // Which side a pin's hover tooltip opens on (flipped near the top edge of the viewport).
        "data-fbk-tip-side",
        // Comma-joined comment ids on a merged pin cluster wrapper (renderPins) — internal selector
        // for toggleClusterMenu(), not a host-facing attribute.
        "data-ids",
    };

    private static readonly string[] StorageKeys =
    {
        "pointer_token",
        "pointer_user",
        "pointer_env_",
        "pointer_toolbar_pos",
        "pointer_widget_theme",
        "pointer_widget_language",
        "pointer_visible",
        "pointer_page_session_id",
        "pointer_lang_reported",
    };

    private static readonly string[] WindowGlobals =
    {
        "window.__pointerEmbedded",
        "window.__POINTER_CONFIG__",
        "window.__POINTER_FETCH__",
    };

    [Fact]
    public void FrozenNames_AreDocumented()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot.Find(), ContractDoc));

        var missing = FrozenNames.Where(n => !doc.Contains(n)).ToList();

        Assert.True(
            missing.Count == 0,
            $"{ContractDoc} is missing frozen name(s): {string.Join(", ", missing)}. "
                + "A new customer-visible name must be added to the doc and to FrozenNames in the same PR."
        );
    }

    [Fact]
    public void ServedFiles_UseOnlyFrozenNames()
    {
        var root = RepoRoot.Find();
        var allowedAttributes = FrozenDomAttributes.Concat(InternalAttributes).ToHashSet();
        var violations = new List<string>();

        // Scope 1 — served/injected surfaces: every data-* literal must be frozen or internal.
        var scope1 = new List<string>();
        scope1.AddRange(Directory.EnumerateFiles(Path.Combine(root, "API", "wwwroot"), "*.md"));
        scope1.AddRange(Directory.EnumerateFiles(Path.Combine(root, "API", "wwwroot"), "*.sh"));
        scope1.Add(Path.Combine(root, "API", "Program.cs"));
        var widgetSrc = Path.Combine(root, "web-component", "src");
        scope1.AddRange(Directory.EnumerateFiles(widgetSrc, "*.ts", SearchOption.AllDirectories));

        foreach (var file in scope1)
        {
            var rel = Path.GetRelativePath(root, file);
            var content = File.ReadAllText(file);
            var isWidgetSource = rel.StartsWith("web-component" + Path.DirectorySeparatorChar);

            foreach (
                var attr in Regex.Matches(content, @"data-[a-z-]+").Select(m => m.Value).Distinct()
            )
                if (!allowedAttributes.Contains(attr))
                    violations.Add(
                        $"{rel}: data attribute '{attr}' is not in {ContractDoc} (frozen table or internal footnote)"
                    );

            if (isWidgetSource)
                foreach (var key in ExtractStorageKeys(content, rel, violations).Distinct())
                    if (!StorageKeys.Contains(key))
                        violations.Add($"{rel}: storage key '{key}' is not in {ContractDoc}");

            if (isWidgetSource || rel == Path.Combine("API", "Program.cs"))
                foreach (
                    var global in Regex
                        .Matches(content, @"window\.__[A-Za-z_]+")
                        .Select(m => m.Value)
                        .Distinct()
                )
                    if (!WindowGlobals.Contains(global))
                        violations.Add($"{rel}: window global '{global}' is not in {ContractDoc}");
        }

        // Scope 2 — marketing pages: only pointer/pf-namespaced data-* attributes are checked
        // (landing/v2/ is excluded; other markup is free).
        foreach (
            var file in Directory.EnumerateFiles(
                Path.Combine(root, "landing"),
                "*.html",
                SearchOption.AllDirectories
            )
        )
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("landing" + Path.DirectorySeparatorChar + "v2" + Path.DirectorySeparatorChar))
                continue;

            var content = File.ReadAllText(file);
            foreach (
                var attr in Regex.Matches(content, @"data-[A-Za-z-]+").Select(m => m.Value).Distinct()
            )
            {
                var lower = attr.ToLowerInvariant();
                if (!lower.Contains("pointer") && !lower.Contains("pf"))
                    continue;
                if (!allowedAttributes.Contains(attr))
                    violations.Add(
                        $"{rel}: pointer-namespaced data attribute '{attr}' is not in {ContractDoc}"
                    );
            }
        }

        Assert.True(
            violations.Count == 0,
            "Customer-visible names outside the frozen contract ("
                + ContractDoc
                + ") — add them to the doc and to this test's allowlists in the same PR:\n"
                + string.Join("\n", violations)
        );
    }

    private static IEnumerable<string> ExtractStorageKeys(
        string content,
        string relPath,
        List<string> violations
    )
    {
        var keys = new List<string>();
        var call = @"(?:localStorage|sessionStorage)\.(?:getItem|setItem|removeItem)";

        foreach (
            var m in Regex
                .Matches(content, call + @"\(\s*['" + "`" + @"]([^'" + "`" + @"]*)")
                .Cast<Match>()
        )
        {
            var literal = m.Groups[1].Value;
            var templateStart = literal.IndexOf("${", StringComparison.Ordinal);
            keys.Add(templateStart >= 0 ? literal[..templateStart] : literal);
        }

        foreach (
            var m in Regex
                .Matches(content, call + @"\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*[,)]")
                .Cast<Match>()
        )
        {
            var identifier = m.Groups[1].Value;
            var declaration = Regex.Match(
                content,
                @"const\s+"
                    + Regex.Escape(identifier)
                    + @"\s*=\s*['" + "`" + @"]([^'" + "`" + @"]+)['" + "`" + @"]"
            );
            if (declaration.Success)
                keys.Add(declaration.Groups[1].Value);
            else
                violations.Add(
                    $"{relPath}: storage key '{identifier}' is neither a literal nor a simple const — the guard test cannot check it"
                );
        }

        return keys;
    }
}
