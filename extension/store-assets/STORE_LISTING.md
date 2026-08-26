# Chrome Web Store listing — Pointer Feedback

## Summary (short description — keep ≤ 132 chars)
Click any element on a running web app, leave a comment, and let AI apply the change to your real source code.

## Category
Developer Tools

## Single purpose description (Privacy practices tab)
Injects the Pointer feedback widget onto any webpage, letting the user click an element and leave
feedback that syncs to their Pointer account. Every requested permission exists solely to support
this: `<all_urls>`/`scripting` inject the widget on the tab the user activates;
`declarativeNetRequestWithHostAccess` strips CSP only on that activated tab so the widget can load on
CSP-strict sites; `storage` holds the session token and remembers the per-domain project choice.

## Detailed description

Pointer turns "make this button bigger" into an actual code change.

Point at any element on a running web app, leave a short comment, and hand the queue to any AI coding tool (Claude Code, Cursor, and more) — it applies the change to the real source files. No more translating vague feedback into code.

This extension carries Pointer onto **any** page — including apps you don't control or can't add a script tag to — and keeps you **logged in once** across every site.

── How it works ──
1. Click the toolbar icon and sign in once.
2. Open any site, pick a project, and Activate — the Pointer widget appears on the page.
3. Click an element, type what should change, submit. The comment is collected on your Pointer server, tagged with project, environment, stakeholder, and author.
4. A developer pulls the queue and lets any AI tool apply the edit to the real source — the comment carries the element's selector, a snapshot, the CSS that actually applies, and the page route, so the change lands precisely.

── Why the extension ──
• Works on any site, even ones with a strict Content-Security-Policy.
• Log in once — no per-site setup, no script tag to install.
• The widget renders in an isolated Shadow DOM, so it never clashes with the page's styles.

── For teams ──
• Stakeholders (clients, PMs, testers) just click and comment on the live app — nothing to install for them beyond this extension.
• Developers get a clean, source-aware queue instead of screenshots and vague notes.

── Privacy & permissions ──
Pointer is powered by **your own Pointer server** (hosted SaaS or self-hosted). Your feedback and login are sent only to the server you configure in the extension's Options.
• Your login token stays inside the extension and is never exposed to the pages you visit.
• Host access (all sites) is used only to inject the widget on tabs you explicitly activate.
• On an activated tab, the extension adjusts that tab's content-security policy solely so the feedback widget can load — scoped to that one tab and removed when you deactivate or close it.

Requires a Pointer account. Set your server URL in the extension Options (defaults to the hosted service).

── Links ──
Website: https://pointer.moamen.work
Docs: https://github.com/moamen-ui/poitner-api#readme
Privacy policy: https://pointer.moamen.work/privacy.html

## Permission justifications (paste into the review form)
- **Host permissions / `<all_urls>`** — to inject the feedback widget into the page on tabs the user explicitly activates. (No narrower match pattern is possible — the user picks which site to activate on at runtime.)
- **`declarativeNetRequestWithHostAccess`** — to remove the page's CSP **only on the user-activated tab** so the widget can load; the rule is scoped to that tab and removed on deactivate/close.
- **`scripting`** — to inject the widget + bridge into the activated tab.
- **`storage`** — to keep the user signed in (session token) and remember per-domain project preferences.

Not requested: `activeTab` and `tabs` — `<all_urls>` already grants unconditional host access to every
tab (a superset of what either would add), so declaring them alongside it would be a redundant/unused
permission, which the review form explicitly flags for rejection. `chrome.tabs.query/reload` and the
`onUpdated`/`onRemoved` listeners work without any `tabs`-family permission.

## Data disclosures (Privacy practices tab)
- Collects: authentication info (login token) and user-submitted content (feedback comments + captured element metadata, including optional console/network context when "Report as a bug" is checked), sent to the user-configured Pointer server.
- Not sold to third parties; not used for advertising or unrelated purposes.
- Privacy policy: https://pointer.moamen.work/privacy.html — **review before publishing** (see note in the file: placeholders for the operator's contact/legal details still need filling in).
