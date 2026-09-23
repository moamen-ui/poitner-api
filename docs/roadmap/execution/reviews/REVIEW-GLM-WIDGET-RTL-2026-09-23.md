# Review — GLM — Widget RTL, localised timeAgo, i18n keys (commit db0dcb7)

- **Reviewer:** GLM (independent front-end/i18n pass)
- **Date:** 2026-09-23
- **Subject:** `db0dcb7` — fix(widget): RTL shadow root for Arabic, localised timeAgo, four hard-coded strings (R5-65 fix-now items 1/3/4, per `docs/runbooks/RTL-AUDIT-2026-09-23.md` §8 rows 1, 3, 4)
- **Scope:** `web-component/src/**` only; regenerated `API/wwwroot/*` artifacts ignored (their gzip numbers are quoted in §5 only because the budget is enforced against them).

## Status

COMPLETE

## 0. Commit vs audit rows (what was promised vs what landed)

| §8 row | Audit ask | Landed | Evidence |
|---|---|---|---|
| 1 (X8) | `dir` on `:host` when widget language is `ar`; audit absolute-positioned bits; launcher mirroring unchanged | Yes, via `applyDir()` reflecting `dir` on the host element + `:host([dir='rtl'])`; SCSS audit **mostly** done (see §1 for misses) | `_base.scss:12-18`, `_launcher.scss:11,35`, `element.ts:762-769` |
| 3 | Translate `timeAgo()` | Yes, via `Intl.RelativeTimeFormat(getLang(), {numeric:'auto'})` | `dom.ts:35-40` |
| 4 | Four hard-coded strings → i18n | Yes, five new keys (the `99+` cap became its own key), all in both catalogs | `i18n.ts` en `:176-180`, `:282-283`, `:335`; ar `:424-425`, `:528-529`, `:581` |

Mechanism check (not asked, but load-bearing): `:host { all: initial; direction: ltr }` then `:host([dir='rtl']) { direction: rtl }` — the explicit `direction` after `all` in the same declaration block wins, and the attribute-prefixed selector outranks the UA's `[dir=rtl]` rule, so the shadow tree's direction is fully author-controlled. `.fbk-launcher { direction: ltr }` (`_launcher.scss:11`) shields the launcher from that inheritance, and `.fbk-rtl { direction: rtl }` (`:35`) re-flips it from the host page's own dir. Coherent.

## 1. Remaining physical properties / transforms under `dir=rtl`

Grepped `translateX`, `translate(`, `left:`, `right:`, `margin-left/right`, `padding-left/right`, `border-left/right`, `text-align` across `web-component/src/styles/`. Clean: no `margin/padding/border-left/right` at all; every `text-align` is `start`/`center`; `float: inline-end` (`_card.scss:87,93`) is logical and mirrors correctly; sidebar slide + `fbk-peek-hide` + close-arrow peek (`_sidebar.scss:20,27,30,37,40,55,73`) and both toggle thumbs (`_sidebar.scss:219-223`, `_popover.scss:216-222`) have explicit `[dir='rtl']` flips; `left: 50%` at `_toast.scss:15` and `_tooltip.scss:20-21` is the deliberate physical centering. **Four misses:**

1. **Pin tooltip anchoring — real bug, same class as the toast/tooltip fix but not converted.** `.fbk-pin-tooltip { inset-inline-start: var(--fbk-tip-x, 50%); transform: translateX(var(--fbk-tip-tx, -50%)) … }` (`_pins.scss:182-183`). Logical 50% anchors from the *physical right* once the shadow root is RTL, while the `--fbk-tip-tx: -50%` translate stays physical — the centred tooltip lands one full tooltip-width (220px) left of its pin. Worse, `data-fbk-tip-align='start'/'end'` (`_pins.scss:208-214`) is computed from *physical* viewport room in `element.ts:3009-3011`, but resolves *logically* — under RTL the edge-clamp picks the opposite side and tooltips can spill off-screen. **Fix:** exactly the commit's own toast/tooltip treatment — `left: var(--fbk-tip-x, 50%)` instead of `inset-inline-start:` in `.fbk-pin-tooltip` (the `start`/`end` var overrides then feed a physical property and match the JS geometry). One line + comment.
2. **Chevron glyph not mirrored.** The sidebar close-arrow keeps `ICON.chevronRight` (`templates.ts:89`); only the peek *transform* flips (`_sidebar.scss:55,73`), so in Arabic the collapse handle slides out on the correct side but its arrow still points right. Audit X5 predicted exactly this ("must mirror if X8 is fixed"). **Fix:** `:host([dir='rtl']) .fbk-sidebar-close-arrow svg { transform: scaleX(-1); }`.
3. **Wrong-side box-shadows.** `box-shadow: -8px 0 24px …` (`_sidebar.scss:14`) and `-2px 0 6px …` (`:66`) cast toward the physical left; under RTL the drawer sits at the physical *left* edge, so its shadow falls off-screen and the page-facing side gets none. **Fix:** `:host([dir='rtl']) .fbk-sidebar { box-shadow: 8px 0 24px … }` (+ `2px 0 6px` for the arrow).
4. **Latent, not live:** `%fbk-tooltip-right/-left` still use `inset-inline-start/end: 101%` (`_tooltip.scss:24-25`); if a side placement is ever chosen from physical viewport space they will mirror under RTL. No template uses anything but `data-placement="top"` today. **Fix:** leave, but add a comment, or make physical.

JS-positioned menus (`element.ts:1294,1396,3136,3192,3291`) anchor from physical `getBoundingClientRect` values — direction-independent, safe. `.fbk-pin-wrapper`'s `translate(-50%, -100%)` (`_pins.scss:17`) is symmetric centering — safe.

## 2. `applyDir()` coverage of every language-changing path

Call sites found: boot pre-shadow `element.ts:265-266`; boot post-`hydrateIdentity`/pre-render `:390-391` (covers extension-injected *and* persisted-token users, since `loadAuth()` ran before); user toggle `setLanguageOverride` `:752-756` (caller `wireLangBtn` `:1331-1332` then re-renders chrome/sidebar/pins — text and dir both refresh). Unit tests cover boot default, `navigator.language` fallback, and the runtime toggle (`widget-improvements.test.ts` "RTL direction" block).

**Gap — post-auth:** `saveAuth()` (`element.ts:677-684`) sets `this.user` (which carries the account's `language`) but never re-runs `setLang(this.resolveLang())`/`applyDir()`; the login callbacks (`:592`, `:698` via `handle401`, `:1099`, `:1103`) go straight to `init()`. An account with `language: 'ar'` on an `en` browser with no local override logs in interactively and stays English/LTR until reload or manual toggle. Pre-existing for *text* (boot path only), but the commit's claim "dir follows the widget's resolved language" inherits the hole. **Fix:** in `saveAuth`, after `this.shortcut = …`: `if (setLang(this.resolveLang())) this.applyDir();` — all interactive-login paths then render correctly via `init()`.

**Launcher never overridden:** `pageIsRtl()` reads `documentElement`/`body` only (`dom.ts:196-205`) and feeds the `.fbk-rtl` class — it cannot see the host element's own `dir`, so no feedback loop; `_launcher.scss:11,35` pin the launcher's direction to the page-driven class regardless of widget language. Page-RTL + widget-en → launcher mirrored, shadow UI LTR; page-LTR + widget-ar → launcher unmirrored, shadow UI RTL. Correct on all four combinations. (Pre-existing limitation, unchanged: a page setting `dir` on a *container* of the widget rather than html/body is not detected.) Setting a light-DOM `dir` on the host is also the right a11y move (standard attribute, understood by AT); the host has no light-DOM children (`appendChild` to `this`/`body`: none), so nothing else inherits it.

## 3. `Intl.RelativeTimeFormat` availability, bad tags, `numeric:'auto'` vs assertions

- **Availability:** Chrome 71+ / Edge 79+ / Firefox 65+ / Safari **14+** (Sep 2020). No browserslist/engines policy exists in the repo to contradict it, but the de-facto syntax floor (`?.`, `??` throughout src, esbuild default target) is Safari 13.1 — so Safari 13.x/iOS 13 plausibly run the widget today and `new Intl.RelativeTimeFormat` there is a **TypeError** thrown inside `timeAgo()` → `TPL.pin` throws → `renderPins()` dies for the whole page, not just one tooltip. There is no try/catch. **Fix (one line):** guard at `dom.ts:35` — `const rtf = typeof Intl.RelativeTimeFormat === 'function' ? new Intl.RelativeTimeFormat(getLang(), { numeric: 'auto' }) : null;` and fall back to the pre-commit English strings (or the plain date) when null. Cheap insurance for a hard failure mode.
- **Unsupported tag:** impossible — `setLang()` normalizes anything ≠ `'ar'` to `'en'` (`i18n.ts:14-21`), so `getLang()` is always `'en'|'ar'`; both are in every browser's built-in locale set (and in full-ICU Node ≥13, which the new exact-Arabic unit assertions already depend on). No fallback-locale surprises.
- **`numeric:'auto'` string changes (en):** "just now"→"now", "1m ago"→"1 minute ago", "2m ago"→"2 minutes ago", "1d ago"→"yesterday". Grepped `e2e/widget/*.spec.ts` for `ago`/`just now`/`yesterday`: **zero assertions** — nothing breaks. The only adjacent regexes are `notifications.spec.ts:107` `/unread/` and `:112` `/(\d+)\+? unread/`, which the new `toolbar.unreadSuffix` (`, {count} unread`) still satisfies in English runs. Unit tests were updated to the new forms (`widget-improvements.test.ts:72-80`), including correct Arabic plurals (دقيقتين / 5 دقائق / 11 دقيقة — verified against CLDR ar rules). `timeAgo` has exactly one call site (`templates.ts:483`, pin hover tooltip) — no other consumer to break.
- Minor, pre-existing and already ticketed as audit X2: the >30-day fallback `new Date(iso).toLocaleDateString()` (`dom.ts:43`) still formats per *browser* locale, not widget language.

## 4. The five new i18n keys in both catalogs; residual hard-coded English

All five keys exist in **both** catalogs — en `toolbar.unreadSuffix`/`toolbar.countCap` (`i18n.ts:176-180`), `card.openFullScreenshot`/`card.elementScreenshot` (`:282-283`), `toast.dismissNotification` (`:335`); ar `toolbar.unreadSuffix`/`toolbar.countCap` (`:424-425`), `card.*` (`:528-529`), `toast.dismissNotification` (`:581`). Unit tests pin both languages (`widget-improvements.test.ts` "resolves the RTL-audit hardcoded strings in English/Arabic"), and the shared `unreadSuffix()` helper (`i18n.ts:121-125`) keeps the two aria-label builders (`templates.ts:80`, `element.ts:1497`) from re-diverging.

Arabic quality: `فتح لقطة الشاشة كاملة`, `لقطة شاشة العنصر`, `إغلاق الإشعار` — correct and idiomatic; `99+` Latin digits consistent with the X1 numeral policy; `، {count} غير مقروءة` uses the correct Arabic comma. One nit: the plural `غير مقروءة` is used for all counts, so count=1 reads `، 1 غير مقروءة` where `غير مقروء واحد` would be exact — acceptable for an aria-label suffix, note for a future plural pass (audit X3).

Residual hard-coded English in scope: **one string** — `element.ts:407` `this.toast('This invite link is invalid or expired — ask for a new one.', 'error')`, reached when a magic-link redemption fails at boot. Not on the audit's four-string list, so not a scope miss by the commit, but it is the last English-only user-visible string in these two files. **Fix:** add `toast.inviteLinkInvalid` en/ar and route it. Also cosmetic-but-worth-fixing doc rot from this very commit: `i18n.ts:1-2` still opens with "the shadow UI stays LTR regardless of language (see `_base.scss`'s `:host { direction: ltr }`)" — now false.

## 5. Gzip budget 64→66 KB: justified or dead weight?

Numbers: widget.js 64,942 → **65,590** gzip (+648 B; `API/wwwroot/pointer.version.json` in this tree), against the new ceiling 67,584 (66 KB) set in both `build.mjs:33` (which stamps `budget.widgetJsGzipMax` into version.json at `:167-168`, so check-budget.mjs's fallback at `:19` really is just a fallback — single source of truth at build time). Headroom is 1,994 B ≈ 3% — tight enough not to hide a future regression.

The +648 B is feature-bearing and plausible: five keys × two catalogs (~260 B raw), the `[dir='rtl']` SCSS blocks (the new verbose `//` comments are stripped by Sass and don't reach the CSS), `applyDir` + `unreadSuffix` + RTF wiring (~250 B). The commit already trimmed real dead weight before raising (verified in-tree: no RTF per-language cache exists; the unread ternary exists once, as the helper). The test file is dev-only, not in the bundle entry. No further dead weight to remove *within this change*. The one honest lever if the next raise is contested is pre-existing and deliberate: `minify: false` (`build.mjs:63`) ships 254,838 raw bytes of readable JS — turning on minify (or just `minifyWhitespace`) would buy back multiples of this raise, at the cost of the readable-artifact policy.

(Parenthetical, artifacts out of scope: the committed `pointer.version.json` is stamped `commit: 6f9320a / 2026-09-22`, which is neither `db0dcb7` nor its parent `bf1c100` — presumably a build-stash tree. The gzip figure matches the commit message, so the budget math stands.)

## Verdict

**SAFE WITH FOLLOW-UPS**

The core mechanism is sound and well-tested for what it set out to do: the dir reflection is coherent with the theme mechanism, the launcher/page-dir separation holds on all four page×language combinations, no e2e assertion breaks, and the budget raise is honest. None of the follow-ups break the default (English) experience; all live behind `lang=ar` or obsolete browsers.

Follow-ups, in order: (1) pin-tooltip physical `left:` fix (`_pins.scss:182`) — row 1's "audit absolute-positioned bits" is not actually complete without it; (2) `saveAuth` re-resolve + `applyDir` (§2); (3) RTF feature guard in `timeAgo` (§3); (4) chevron/shadow mirroring + `toast.inviteLinkInvalid` + the stale `i18n.ts` header comment (§1/§4). Item (1) is the only one I'd want in before calling row 1 done.

**Riskiest single change:** `_base.scss:20,26` — `* { direction: inherit }` behind `:host([dir='rtl'])`. It flips the resolution of *every* logical property in the entire shadow tree in one move, and the safety of the flip depends on an exhaustive manual audit of every physical/logical pairing in the tree. The commit's own toast/tooltip fix proves the audit method is right; the pin-tooltip miss proves it is not yet exhaustive — that asymmetry (one global switch, N audited sites) is where every residual bug in this review lives.
