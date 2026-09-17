# Widget design brief — `<pointer-feedback>`

> Paste everything below the line into Claude Design or Google Stitch. It needs no repo access.
> Source of truth for what exists today: `web-component/src/templates.ts`, `styles/*.scss`; brand:
> `pointer-dashboard/DESIGN.md`. Written 2026-09-16.

---

## Role

You are a senior product designer redesigning an **embeddable feedback widget** that lives on top of
other people's web apps. Quality bar: Linear's command palette, Vercel's toolbar, Figma's comment
pins — quiet, precise, obviously well-made, never louder than the host page. It must feel like part of
the product it sits on while staying unmistakably one system.

## What the widget is

**Pointer** turns element-level feedback into AI-ready work. A signed-in reviewer (developer, PM,
tester, or an invited client) opens the widget on a running app, clicks any element, writes a
comment, and Pointer captures the element (selector, DOM snippet, applied CSS, source file when known,
route, viewport) and optionally a screenshot and the page's console/network context. Developers then
run one CLI command and their AI coding tool applies the fix; the comment moves through
**Open → Ready → Applied → Live → Verified**, and the reviewer confirms with 👍 / 👎 in the same widget.

The widget is a Web Component rendered in a Shadow DOM over the host page. It is delivered either by
a script tag in the app or by a Chrome extension that injects the very same widget onto any page.

## Who uses it, in priority order

1. **Reviewers** (PM, tester, client) — comment, reply, verify. Often non-technical. Sometimes on a phone.
2. **Developers** — triage: mark Ready, mark Completed, reopen, archive, switch environment, read the captured context.
3. **Admins** — one extra control (commit style) and the ability to verify any comment.

## Surfaces to design (all states)

Design each as a component with its states; screens come from composing them.

1. **Launcher** — collapsed state. 52px circle today, corner-anchored (start/end aware), badge for
   comment count or unread updates. Needs a subtle attention pulse on first appearance only.
2. **Toolbar** — floating, draggable (grip), frosted glass today. Buttons: Comment on an element
   (inspect), Comments (with count), Updates (with unread badge), account, Hide, and a reset-position
   affordance that appears only after dragging. Design a compact and an expanded variant.
3. **Inspect mode** — hover highlight on host elements (dashed outline + crosshair today), a hint toast
   "Click any element… Esc to cancel", and how the widget's own chrome recedes while picking.
4. **Composer popover** — anchored to the clicked point. Shows the element tag, a trimmed DOM snippet,
   the resolved source path when known, a textarea, optional predefined prompts (checkbox list from the
   project), and three toggles: Attach screenshot, Report as a bug (adds console/network context),
   Keep private. Buttons Add / Cancel. States: empty, typing, screenshot capturing, submitting, error.
   On phones this must become a **bottom sheet**, not a floating box.
5. **Comments panel** — right drawer, 360px today (max 92vw). Header: title, close. Row: project name,
   **environment switcher** (select, or a read-only label when fixed), refresh. Admin-only commit-style
   control (One commit / Separate commits). Filters: status (with counts), author (only when several),
   "Mine only". Then the list. States: loading, empty (no comments yet), filtered-empty, error
   ("Could not reach server"), signed-out prompt.
6. **Comment card** — the heart of the product. Data: status pill (none for Open; Pending amber;
   Completed green; **Live** green with deploy sha on hover; Archived muted), environment pill, private
   lock, body, author, date, "edited" marker, applied-by label, commit link pill (inert "#" when none),
   optional advisory "⚠ looks like a secret" pill, screenshot thumbnail (opens full), replies (human
   vs AI replies visually distinct), reply input. Actions, role-gated: Ready toggle, Mark completed,
   Reopen, Archive, Edit (inline textarea + "Remove image"), Delete (two-step inline confirm),
   Private/Public. **Verification**: on an Applied, unverified card the author or an admin sees
   👍 Looks right / 👎 Not fixed; 👎 opens a note box (required) — after either, a "✓ Verified" pill.
   Design the card for: mine vs someone else's, quick-access client (fewer actions), each status,
   verified/unverified, with/without screenshot, long body, RTL body.
7. **Pins** — one per open/pending comment anchored to its element on the page, numbered, colored by
   status; clicking opens the panel and scrolls to the card. Design collision/overlap behaviour and the
   hover tooltip.
8. **Updates menu** — dropdown from the toolbar: Applied / Reopened / New reply items with excerpt,
   time, and detail (commit link or reply excerpt); unread emphasis; empty state.
9. **Account menu** — identity + role, the "Add comment" keyboard shortcut (rebind / reset), Sign out;
   in extension mode a note replaces Sign out because the extension owns the session.
10. **Sign-in modal** — email/password, error, "Create account", sign-up (name, email, password,
    role), a "request access again" state for rejected accounts, and a **Skip for now** escape. In
    extension mode this modal never appears.
11. **Toasts** — bottom-center today, three variants; design stacking for rapid actions.
12. **Environment switcher and status filter** as compact controls that survive a 360px panel.

## The end-to-end flow to make legible

Launcher → toolbar → Comment on an element → inspect → click → composer → Add → pin + card appear →
developer marks Ready → (outside the widget: CLI + AI tool apply and commit) → card shows Completed
with the commit link → after deploy, Live → reviewer clicks 👍 or 👎 → Verified. The widget should make
"where is my comment in this journey" answerable at a glance.

## Brand system to reuse — "The Review Margin"

Same world as the dashboard, so reviewers moving between them feel one product.

- Type: **IBM Plex Sans Arabic** for UI, **IBM Plex Mono** for selectors, source paths, shas, codes.
  Constraint: the widget cannot load web fonts on a host page (size and CSP), so specify the system
  fallback stack and design so the layout holds on it.
- Brand blue `#0969da` (dark `#4493f8`) with the **four-jobs rule**: primary action, focus ring,
  active state, links. Nothing else is blue.
- State hues, fixed: Open `#0969da`, Ready `#9a6700`, Applied/Live `#1a7f37`, Archived `#59636e`,
  Danger `#d1242f`. Neutrals: surfaces `#ffffff`/`#0d1117`, panels `#f6f8fa`/`#161b22`, text
  `#1f2328`/`#e6edf3`, hairline `#d0d7de`/`#30363d`. Radii 6px; 8–10px for floating surfaces.
- Map every color you use onto the widget's existing token names so engineering can wire it without
  redesign: `--fbk-primary`, `--fbk-primary-hover`, `--fbk-primary-contrast`, `--fbk-surface`,
  `--fbk-surface-alt`, `--fbk-surface-muted`, `--fbk-surface-hover`, `--fbk-border`, `--fbk-border-soft`,
  `--fbk-border-strong`, `--fbk-text`, `--fbk-text-muted`, `--fbk-text-subtle`, `--fbk-success*`,
  `--fbk-warn*`, `--fbk-danger*`, `--fbk-info-bg/text`, `--fbk-overlay`, `--fbk-shadow`,
  `--fbk-toolbar-bg/border`, `--fbk-radius-sm/md/lg/xl`. Propose new tokens only for spacing and type
  scale (none exist today) and for dark mode.
- Host apps override tokens per project (e.g. `pointer-feedback { --fbk-primary: #0aa36e }`), so the
  design must survive a different primary without breaking contrast.

## Hard constraints

- **Shadow DOM overlay on someone else's page.** Nothing may depend on the host's CSS. Every surface
  is `position: fixed` at maximum z-index. The widget must never block the host page except where a
  panel is open, and must work over host modals and sticky headers.
- **Budget.** The whole widget JS ships under 60 KB gzipped; CSS is a single file. No illustrations,
  no icon fonts, no web fonts, no images except the reviewer's screenshots. Icons are inline SVG
  strokes, 16px grid.
- **Light and dark**, both first-class. Today the widget has no dark mode — design it. Default to
  following the host's `prefers-color-scheme`, with a host override.
- **RTL and Arabic**, first-class. Today the widget forces LTR — design mirrored layouts and Arabic
  copy for every surface; selectors, paths and codes stay LTR inline.
- **Mobile.** No breakpoints exist today. Design 360px: composer as bottom sheet, panel full-width,
  44px targets, toolbar that does not cover content, pins that stay tappable.
- **Accessibility.** Focus trap in modal and composer, `role="dialog"`, live region for toasts and
  status changes, visible focus rings (brand blue), keyboard: Esc closes, Enter submits reply,
  global shortcut `Ctrl+Alt+Shift+C` rebindable.
- **Strict CSP.** No inline styles; state is expressed by classes. Nothing you design may need
  runtime-injected style attributes.
- Copy is short, tool-agnostic ("your AI tool", never a vendor name), and white-label: the product
  name comes from the server and may not be "Pointer".

## Known problems to solve, not preserve

No dark mode; forced LTR; popover is a floating box on phones; toasts overlap under rapid actions;
sidebar and modal lack dialog semantics and focus trapping; toolbar and drawer have no size variants
for small screens; the compact `<select>` filters were a space trade-off and may become chips or tabs
if they fit 360px; the bug-report and screenshot toggles read as an afterthought and deserve clearer
affordance since they are the difference between a vague note and an actionable brief.

## Deliverables

1. **Screens**: light and dark, en and ar, at 1440 and 390: launcher; toolbar; inspect mode over a
   sample app; composer (desktop popover and mobile sheet, all states); comments panel with a mixed
   list (every status, verified and not, mine and others, with a screenshot); a single card at 1:1
   with annotations; updates menu; account menu; sign-in modal; toasts.
2. **Component spec**: anatomy, states, sizes, spacing, and behaviour notes per surface.
3. **Token sheet**: light and dark values for every `--fbk-*` name above plus the new spacing and type
   tokens, with contrast checks against a white and a dark host.
4. **Motion spec**: launcher appear, drawer slide, popover open, pin pulse, toast in/out, verify
   feedback — durations and easings, all under 250ms, reduced-motion behaviour.
5. **Design notes**: decisions and rationale, one page.

## Process

Start with three directions for the card and composer only, one page each, then converge. Then build
the full set. Finish with one verification pass against the constraints list above and report what
you could not satisfy rather than bending a constraint silently.
