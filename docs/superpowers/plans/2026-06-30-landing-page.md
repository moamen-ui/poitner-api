# Pointer Landing Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a single-page, bilingual (en/ar) marketing landing page for Pointer at the bare domain `pointer.moamen.work`, and decommission the legacy Netlify backend.

**Architecture:** One self-contained static `index.html` (inline `<style>` + inline `<script>`, no framework, no build step) lives in `pointer-api/landing/`, is copied to `/srv/landing` on the VM, and is served by Caddy at `pointer.moamen.work` (changed from a redirect-to-app). A separate workstream in the legacy `Pointer` repo removes the Netlify files and corrects the docs.

**Tech Stack:** Plain HTML5 + CSS (flex/grid, logical properties for RTL) + vanilla JS (i18n dictionary, language toggle, scripted hero animation). Caddy static file server. No npm, no dependencies.

## Global Constraints

- **Two repos.** Landing page + Caddy/deploy live in `pointer-api` (the VM pulls this). Netlify removal + doc cleanup live in `Pointer` (`/Users/momen/Desktop/REPOS/Pointer`).
- **Zero build step / zero dependencies** for the landing page — one self-contained `index.html`; CSS and JS inline; brand assets sit alongside in `pointer-api/landing/assets/` or are inlined as data URIs.
- **CTAs (exact URLs):** "Try the demo" → `https://demo.pointer.moamen.work`; "Sign in" and "Create account" → `https://app.pointer.moamen.work`.
- **Bilingual:** English + Arabic. Default English; if `navigator.language` starts with `ar`, start in Arabic. A persisted choice (`localStorage` key `pointer_lang`) always wins. Toggle in the nav. Arabic sets `<html lang="ar" dir="rtl">`.
- **Brand:** green→blue gradient (`#16a34a` → `#2563eb`), dog mascot (`assets/dog-mascot.png` from the `Pointer` repo root), light theme (slate `#0f172a` text on white / soft-gradient sections), `system-ui` font stack.
- **Accessibility/responsive:** honors `prefers-reduced-motion` (static animation frame); fully responsive mobile→desktop; the page body must never scroll horizontally.
- **GitHub footer link:** `https://github.com/moamen-ui` (confirm the exact repo with the user at build time; if repos are private, the link target is still the org page).
- **Bare domain switch:** `pointer.moamen.work` stops 301-redirecting to the app and serves the landing page. `app.pointer` and `demo.pointer` are unchanged.
- **No deploy until the user's explicit go.** Build + verify locally first.
- **Verification, not unit tests:** this is a static page with no JS test framework. Each task's "test" is a concrete render/behavior check (grep the HTML, serve it on `python3 -m http.server 8799`, and/or a browser check). Use the browser tooling available to the worker; fall back to `curl`/`grep` where a headless check suffices.

---

## File Structure

**`pointer-api` repo:**
- Create: `landing/index.html` — the entire landing page (HTML + inline CSS + inline JS).
- Create: `landing/assets/dog-mascot.png` — copied from `Pointer/dog-mascot.png` (or a resized copy).
- Create: `landing/assets/favicon.ico` — copied from `Pointer/favicon.ico`.
- Modify: `Caddyfile` — replace the `pointer.moamen.work` redirect block with a static file server.
- Modify: `DEPLOY.md` — document the landing build/deploy step.

**`Pointer` repo (Netlify decommission):**
- Delete: `netlify.toml`, `netlify/functions/api.mjs` (+ the `netlify/` dir), root `package.json`, `public/index.html` (+ `public/` if otherwise empty of source).
- Modify: `.gitignore` (drop the `.netlify/` line), `README.md`, `AGENTS.md`, `CLAUDE.md`.

---

## Task 1: Page scaffold — `<head>`, brand tokens, sticky nav

**Files:**
- Create: `pointer-api/landing/index.html`

**Interfaces:**
- Produces: the document skeleton with CSS custom properties `--pf-green`, `--pf-blue`, `--pf-text`, `--pf-bg`, gradient helpers, and a sticky `<header class="nav">` containing the logo, anchor links, a language toggle button `#lang-toggle`, and the two CTAs. Later tasks append `<section>`s into `<main>` and i18n attributes.

- [ ] **Step 1: Create `landing/index.html` with head + brand tokens + nav**

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Pointer — click-to-comment feedback your team turns into code with AI</title>
  <meta name="description" content="Pointer lets anyone on your team click an element on a running app and leave a comment. Developers hand the queue to any AI tool, which applies the change to the real source." />
  <link rel="icon" href="assets/favicon.ico" />
  <style>
    :root {
      --pf-green: #16a34a;
      --pf-blue: #2563eb;
      --pf-text: #0f172a;
      --pf-muted: #475569;
      --pf-bg: #ffffff;
      --pf-soft: #f1f5f9;
      --pf-border: #e2e8f0;
      --pf-grad: linear-gradient(135deg, var(--pf-green), var(--pf-blue));
      --pf-radius: 14px;
      --pf-max: 1080px;
    }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; }
    body {
      font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
      color: var(--pf-text);
      background: var(--pf-bg);
      line-height: 1.6;
      overflow-x: hidden;            /* never scroll the body sideways */
    }
    img { max-width: 100%; display: block; }
    a { color: var(--pf-blue); text-decoration: none; }
    .wrap { max-width: var(--pf-max); margin: 0 auto; padding: 0 20px; }
    .btn {
      display: inline-flex; align-items: center; gap: 8px;
      padding: 11px 18px; border-radius: var(--pf-radius);
      font-weight: 700; font-size: 15px; cursor: pointer; border: 1px solid var(--pf-border);
      background: #fff; color: var(--pf-text); white-space: nowrap;
    }
    .btn.primary { background: var(--pf-grad); color: #fff; border: none; }
    .btn:focus-visible { outline: 3px solid color-mix(in srgb, var(--pf-blue) 50%, transparent); }
    /* Sticky nav */
    .nav {
      position: sticky; top: 0; z-index: 10;
      background: color-mix(in srgb, #fff 88%, transparent);
      backdrop-filter: saturate(1.4) blur(8px);
      border-bottom: 1px solid var(--pf-border);
    }
    .nav .wrap { display: flex; align-items: center; gap: 16px; height: 64px; }
    .brand { display: flex; align-items: center; gap: 10px; font-weight: 800; font-size: 18px; }
    .brand img { width: 30px; height: 30px; }
    .nav-links { display: flex; gap: 18px; margin-inline-start: auto; }
    .nav-links a { color: var(--pf-muted); font-weight: 600; font-size: 14px; }
    .nav-cta { display: flex; gap: 10px; align-items: center; }
    #lang-toggle { background: var(--pf-soft); border: 1px solid var(--pf-border); border-radius: 999px; padding: 6px 12px; font-weight: 700; cursor: pointer; }
    @media (max-width: 760px) {
      .nav-links { display: none; }
    }
  </style>
</head>
<body>
  <header class="nav">
    <div class="wrap">
      <div class="brand"><img src="assets/dog-mascot.png" alt="" /> <span>Pointer</span></div>
      <nav class="nav-links">
        <a href="#how" data-i18n="nav.how">How it works</a>
        <a href="#features" data-i18n="nav.features">Features</a>
        <a href="#demo" data-i18n="nav.demo">Demo</a>
      </nav>
      <div class="nav-cta">
        <button id="lang-toggle" type="button" aria-label="Switch language">العربية</button>
        <a class="btn" href="https://app.pointer.moamen.work" data-i18n="nav.signin">Sign in</a>
        <a class="btn primary" href="https://demo.pointer.moamen.work" data-i18n="nav.demoCta">Try the demo</a>
      </div>
    </div>
  </header>

  <main></main>

  <script>
    // i18n + animation wiring added in later tasks.
  </script>
</body>
</html>
```

- [ ] **Step 2: Copy brand assets into place**

Run:
```bash
mkdir -p /Users/momen/Desktop/REPOS/pointer-api/landing/assets
cp /Users/momen/Desktop/REPOS/Pointer/dog-mascot.png /Users/momen/Desktop/REPOS/pointer-api/landing/assets/dog-mascot.png
cp /Users/momen/Desktop/REPOS/Pointer/favicon.ico /Users/momen/Desktop/REPOS/pointer-api/landing/assets/favicon.ico
```
Expected: both files exist under `landing/assets/`.

- [ ] **Step 3: Serve and verify it loads**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing && python3 -m http.server 8799 >/dev/null 2>&1 &
sleep 1
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8799/index.html
curl -s http://localhost:8799/index.html | grep -c 'Try the demo'
```
Expected: `200`, and `1` (the demo CTA present). Then stop the server: `pkill -f "http.server 8799"`.

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html landing/assets/
git commit -m "feat(landing): page scaffold — head, brand tokens, sticky nav"
```

---

## Task 2: Hero section + CTAs

**Files:**
- Modify: `pointer-api/landing/index.html` (insert into `<main>`, add CSS in the `<style>` block)

**Interfaces:**
- Consumes: brand tokens + `.btn` styles from Task 1.
- Produces: `<section class="hero">` with `#hero-anim` (an empty stage the animation in Task 4 fills), headline, subhead, and the two CTAs.

- [ ] **Step 1: Add hero markup as the first child of `<main>`**

```html
<section class="hero">
  <div class="wrap hero-grid">
    <div class="hero-copy">
      <h1 data-i18n="hero.title">Point at the UI. Ship it with AI.</h1>
      <p class="lead" data-i18n="hero.sub">Anyone on your team clicks an element on a running app and leaves a short comment. Developers hand the queue to any AI tool, which applies the change to the real source — no more translating vague feedback into code.</p>
      <div class="hero-cta">
        <a class="btn primary" href="https://demo.pointer.moamen.work" data-i18n="hero.demo">Try the demo — no install</a>
        <a class="btn" href="https://app.pointer.moamen.work" data-i18n="hero.signup">Create an account</a>
      </div>
    </div>
    <div class="hero-stage" id="hero-anim" aria-hidden="true"></div>
  </div>
</section>
```

- [ ] **Step 2: Add hero CSS to the `<style>` block (before the closing `</style>`)**

```css
.hero { padding: 64px 0 48px; background: radial-gradient(1200px 400px at 70% -10%, color-mix(in srgb, var(--pf-blue) 12%, transparent), transparent); }
.hero-grid { display: grid; grid-template-columns: 1.1fr 1fr; gap: 40px; align-items: center; }
.hero h1 { font-size: clamp(30px, 5vw, 52px); line-height: 1.1; margin: 0 0 16px; letter-spacing: -0.02em; }
.hero .lead { font-size: 18px; color: var(--pf-muted); margin: 0 0 28px; }
.hero-cta { display: flex; flex-wrap: wrap; gap: 12px; }
.hero-stage { min-height: 320px; }
@media (max-width: 860px) {
  .hero-grid { grid-template-columns: 1fr; }
  .hero-stage { order: -1; min-height: 240px; }
}
```

- [ ] **Step 3: Verify CTAs point to the right hosts**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing
grep -o 'https://demo.pointer.moamen.work' index.html | head -1
grep -o 'https://app.pointer.moamen.work' index.html | head -1
```
Expected: each prints its URL (both CTAs wired).

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): hero section + CTAs"
```

---

## Task 3: Content sections — problem→solution, how-it-works, features, team split, final CTA, footer

**Files:**
- Modify: `pointer-api/landing/index.html`

**Interfaces:**
- Consumes: `.wrap`, `.btn`, brand tokens.
- Produces: `<section>`s with `id="how"`, `id="features"`, `id="demo"`, and a `<footer>` containing the GitHub link. All user-facing strings carry `data-i18n` keys (defined in Task 5).

- [ ] **Step 1: Append the sections after the hero, inside `<main>`**

```html
<!-- Problem → solution -->
<section class="band">
  <div class="wrap">
    <div class="contrast">
      <div class="contrast-card bad">
        <h3 data-i18n="contrast.badTitle">Without Pointer</h3>
        <p data-i18n="contrast.bad">"Go to the checkout page, find the header, make the title 24px and a bit darker…"</p>
      </div>
      <div class="contrast-card good">
        <h3 data-i18n="contrast.goodTitle">With Pointer</h3>
        <p data-i18n="contrast.good">🐕 click the title → 💬 "make this 24px" → ✨ AI applies it.</p>
      </div>
    </div>
  </div>
</section>

<!-- How it works -->
<section id="how" class="wrap section">
  <h2 class="section-title" data-i18n="how.title">How it works</h2>
  <div class="steps">
    <div class="step"><span class="num">1</span><h3 data-i18n="how.s1t">Click & comment</h3><p data-i18n="how.s1">Anyone signed in clicks any element on the running app and leaves a short comment.</p></div>
    <div class="step"><span class="num">2</span><h3 data-i18n="how.s2t">Collected & tagged</h3><p data-i18n="how.s2">Comments are stored per project and tagged with environment, stakeholder, and author.</p></div>
    <div class="step"><span class="num">3</span><h3 data-i18n="how.s3t">Applied by AI</h3><p data-i18n="how.s3">A developer hands the queue to any AI tool, which edits the real source files.</p></div>
  </div>
</section>

<!-- Features -->
<section id="features" class="band">
  <div class="wrap section">
    <h2 class="section-title" data-i18n="feat.title">Built for real teams</h2>
    <div class="features">
      <div class="feature"><h3 data-i18n="feat.f1t">Two-line install</h3><p data-i18n="feat.f1">Drop a script tag and a tag. No package, no SDK.</p></div>
      <div class="feature"><h3 data-i18n="feat.f2t">Multi-project</h3><p data-i18n="feat.f2">One server serves many apps, partitioned by project.</p></div>
      <div class="feature"><h3 data-i18n="feat.f3t">Multi-stakeholder</h3><p data-i18n="feat.f3">Every comment is tagged by environment, stakeholder, and author.</p></div>
      <div class="feature"><h3 data-i18n="feat.f4t">Element + source aware</h3><p data-i18n="feat.f4">Captures the selector, snapshot, the CSS that wins, the page route, and an optional source path.</p></div>
      <div class="feature"><h3 data-i18n="feat.f5t">AI-agnostic</h3><p data-i18n="feat.f5">Any AI tool applies the changes with plain HTTP — Claude Code, Cursor, and more.</p></div>
      <div class="feature"><h3 data-i18n="feat.f6t">Style-isolated</h3><p data-i18n="feat.f6">The widget renders in a Shadow DOM, so it never clashes with your app's CSS.</p></div>
      <div class="feature"><h3 data-i18n="feat.f7t">A real dashboard</h3><p data-i18n="feat.f7">Triage, statuses, roles, and per-project views — for the whole team.</p></div>
      <div class="feature"><h3 data-i18n="feat.f8t">Multi-tenant</h3><p data-i18n="feat.f8">Each workspace sees only its own data, enforced server-side.</p></div>
    </div>
  </div>
</section>

<!-- Built for the whole team -->
<section class="wrap section">
  <div class="split">
    <div class="split-card"><h3 data-i18n="team.stkT">For stakeholders</h3><p data-i18n="team.stk">Point and comment on the live app — no install, no setup. Clients, PMs, and testers just click.</p></div>
    <div class="split-card"><h3 data-i18n="team.devT">For developers</h3><p data-i18n="team.dev">Pull the queue and let any AI apply the changes to the real source. Comments carry the page route and the CSS that actually wins.</p></div>
  </div>
</section>

<!-- Final CTA -->
<section id="demo" class="cta-band">
  <div class="wrap">
    <h2 data-i18n="final.title">Try it in one click — no install</h2>
    <div class="hero-cta" style="justify-content:center">
      <a class="btn primary" href="https://demo.pointer.moamen.work" data-i18n="final.demo">Try the demo</a>
      <a class="btn" href="https://app.pointer.moamen.work" data-i18n="final.signup">Create an account</a>
    </div>
  </div>
</section>

<footer class="footer">
  <div class="wrap footer-grid">
    <div class="brand"><img src="assets/dog-mascot.png" alt="" /> <span>Pointer</span></div>
    <div class="footer-links">
      <a href="https://app.pointer.moamen.work" data-i18n="foot.dashboard">Dashboard</a>
      <a href="https://api.pointer.moamen.work/pointer-init.md" data-i18n="foot.docs">Docs</a>
      <a href="https://github.com/moamen-ui" rel="noopener" target="_blank">GitHub</a>
    </div>
  </div>
</footer>
```

- [ ] **Step 2: Append section CSS to the `<style>` block**

```css
.section { padding: 56px 0; }
.section-title { font-size: clamp(24px, 3vw, 34px); margin: 0 0 28px; text-align: center; }
.band { background: var(--pf-soft); }
.contrast { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; padding: 40px 0; }
.contrast-card { background: #fff; border: 1px solid var(--pf-border); border-radius: var(--pf-radius); padding: 20px; }
.contrast-card.bad { color: var(--pf-muted); }
.contrast-card.good { border-color: color-mix(in srgb, var(--pf-green) 50%, var(--pf-border)); }
.steps, .features { display: grid; gap: 18px; }
.steps { grid-template-columns: repeat(3, 1fr); }
.features { grid-template-columns: repeat(4, 1fr); }
.step, .feature { background: #fff; border: 1px solid var(--pf-border); border-radius: var(--pf-radius); padding: 20px; }
.step .num { display: inline-grid; place-items: center; width: 30px; height: 30px; border-radius: 50%; background: var(--pf-grad); color: #fff; font-weight: 800; margin-bottom: 10px; }
.step h3, .feature h3 { margin: 0 0 6px; font-size: 17px; }
.step p, .feature p { margin: 0; color: var(--pf-muted); font-size: 14px; }
.split { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.split-card { padding: 24px; border-radius: var(--pf-radius); border: 1px solid var(--pf-border); background: #fff; }
.cta-band { background: var(--pf-grad); color: #fff; text-align: center; padding: 56px 0; }
.cta-band h2 { font-size: clamp(24px, 3.4vw, 36px); margin: 0 0 22px; }
.footer { border-top: 1px solid var(--pf-border); padding: 28px 0; }
.footer-grid { display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
.footer-links { display: flex; gap: 18px; }
.footer-links a { color: var(--pf-muted); font-weight: 600; }
@media (max-width: 860px) {
  .steps { grid-template-columns: 1fr; }
  .features { grid-template-columns: 1fr 1fr; }
  .contrast, .split { grid-template-columns: 1fr; }
}
@media (max-width: 520px) {
  .features { grid-template-columns: 1fr; }
}
```

- [ ] **Step 3: Verify structure + GitHub link present**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing
grep -c 'id="how"\|id="features"\|id="demo"' index.html
grep -c 'github.com/moamen-ui' index.html
```
Expected: `3` (the three anchor targets) and `1` (GitHub link).

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): content sections + footer with GitHub link"
```

---

## Task 4: Scripted hero animation + reduced-motion fallback

**Files:**
- Modify: `pointer-api/landing/index.html` (CSS in `<style>`, markup inside `#hero-anim`, JS in the `<script>` is NOT required — the loop is CSS-driven)

**Interfaces:**
- Consumes: `#hero-anim` from Task 2.
- Produces: a self-running CSS animation (mock app card + cursor + highlight + typed popover + dropped pin). No backend. A `@media (prefers-reduced-motion: reduce)` rule freezes it to a representative static frame.

- [ ] **Step 1: Replace the empty `#hero-anim` with the mock-UI markup**

```html
<div class="hero-stage" id="hero-anim" aria-hidden="true">
  <div class="mock">
    <div class="mock-bar"><span></span><span></span><span></span></div>
    <button class="mock-target">Buy now</button>
    <div class="mock-text"></div>
    <div class="mock-text short"></div>
    <span class="pf-cursor"></span>
    <span class="pf-ring"></span>
    <div class="pf-pop"><b data-i18n="anim.label">Make this bolder</b></div>
    <span class="pf-pin">1</span>
  </div>
</div>
```

- [ ] **Step 2: Add the animation CSS to the `<style>` block**

```css
.mock { position: relative; background: #fff; border: 1px solid var(--pf-border); border-radius: var(--pf-radius); box-shadow: 0 20px 50px rgba(2,6,23,.12); padding: 22px; height: 320px; overflow: hidden; }
.mock-bar { display: flex; gap: 6px; margin-bottom: 18px; }
.mock-bar span { width: 11px; height: 11px; border-radius: 50%; background: var(--pf-border); }
.mock-target { font: inherit; font-weight: 700; padding: 10px 16px; border-radius: 10px; border: none; color: #fff; background: var(--pf-grad); }
.mock-text { height: 12px; border-radius: 6px; background: var(--pf-soft); margin-top: 16px; }
.mock-text.short { width: 60%; }
.pf-cursor { position: absolute; width: 18px; height: 18px; border-radius: 50%; border: 2px solid var(--pf-blue); background: rgba(37,99,235,.2); top: 60%; left: 60%; animation: pf-cursor 6s ease-in-out infinite; }
.pf-ring { position: absolute; left: 22px; top: 56px; width: 96px; height: 42px; border: 2px solid var(--pf-blue); border-radius: 10px; opacity: 0; animation: pf-ring 6s ease-in-out infinite; }
.pf-pop { position: absolute; left: 130px; top: 52px; background: #0f172a; color: #fff; font-size: 13px; padding: 8px 12px; border-radius: 10px; opacity: 0; transform: translateY(4px); animation: pf-pop 6s ease-in-out infinite; white-space: nowrap; }
.pf-pin { position: absolute; left: 96px; top: 44px; width: 22px; height: 22px; border-radius: 50% 50% 50% 2px; background: var(--pf-grad); color: #fff; font-size: 12px; font-weight: 800; display: grid; place-items: center; opacity: 0; animation: pf-pin 6s ease-in-out infinite; }
@keyframes pf-cursor {
  0% { top: 75%; left: 70%; } 25% { top: 60px; left: 56px; } 100% { top: 60px; left: 56px; }
}
@keyframes pf-ring { 0%,18% { opacity: 0; } 28%,100% { opacity: 1; } }
@keyframes pf-pop { 0%,30% { opacity: 0; transform: translateY(4px); } 42%,92% { opacity: 1; transform: translateY(0); } 100% { opacity: 0; } }
@keyframes pf-pin { 0%,40% { opacity: 0; transform: scale(.4); } 52%,100% { opacity: 1; transform: scale(1); } }
@media (prefers-reduced-motion: reduce) {
  .pf-cursor, .pf-ring, .pf-pop, .pf-pin { animation: none; }
  .pf-ring, .pf-pop, .pf-pin { opacity: 1; }       /* show the representative end frame */
  .pf-cursor { top: 60px; left: 56px; }
}
```

- [ ] **Step 2b: Verify reduced-motion static frame in a browser**

Serve (`python3 -m http.server 8799` in `landing/`). In the browser worker, emulate reduced motion and confirm the pin/popover/ring are visible (static) and no animation runs. If only `curl` is available, verify the media query exists:
```bash
grep -c 'prefers-reduced-motion: reduce' /Users/momen/Desktop/REPOS/pointer-api/landing/index.html
```
Expected: `1`.

- [ ] **Step 3: Verify the animation renders without console errors**

Serve and open `http://localhost:8799/` in the browser worker; confirm the mock card, cursor, popover, and pin elements exist in the DOM and there are no console errors. Stop the server afterwards.

- [ ] **Step 4: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): scripted hero animation + reduced-motion fallback"
```

---

## Task 5: Bilingual i18n (en/ar), toggle, RTL, persistence, auto-detect

**Files:**
- Modify: `pointer-api/landing/index.html` (fill the inline `<script>`, add RTL CSS)

**Interfaces:**
- Consumes: every element carrying a `data-i18n="<key>"` attribute from Tasks 1–4.
- Produces: a `STRINGS` dictionary with `en` and `ar` for every key used, an `applyLang(lang)` function (sets `textContent` for each `[data-i18n]`, sets `<html lang>`/`dir`, updates the toggle label, persists to `localStorage`), and the toggle wiring + first-load language resolution.

- [ ] **Step 1: Fill the inline `<script>` with the dictionary + apply logic**

```html
<script>
  var STRINGS = {
    en: {
      "nav.how": "How it works", "nav.features": "Features", "nav.demo": "Demo",
      "nav.signin": "Sign in", "nav.demoCta": "Try the demo",
      "hero.title": "Point at the UI. Ship it with AI.",
      "hero.sub": "Anyone on your team clicks an element on a running app and leaves a short comment. Developers hand the queue to any AI tool, which applies the change to the real source — no more translating vague feedback into code.",
      "hero.demo": "Try the demo — no install", "hero.signup": "Create an account",
      "contrast.badTitle": "Without Pointer", "contrast.bad": "\"Go to the checkout page, find the header, make the title 24px and a bit darker…\"",
      "contrast.goodTitle": "With Pointer", "contrast.good": "🐕 click the title → 💬 \"make this 24px\" → ✨ AI applies it.",
      "how.title": "How it works",
      "how.s1t": "Click & comment", "how.s1": "Anyone signed in clicks any element on the running app and leaves a short comment.",
      "how.s2t": "Collected & tagged", "how.s2": "Comments are stored per project and tagged with environment, stakeholder, and author.",
      "how.s3t": "Applied by AI", "how.s3": "A developer hands the queue to any AI tool, which edits the real source files.",
      "feat.title": "Built for real teams",
      "feat.f1t": "Two-line install", "feat.f1": "Drop a script tag and a tag. No package, no SDK.",
      "feat.f2t": "Multi-project", "feat.f2": "One server serves many apps, partitioned by project.",
      "feat.f3t": "Multi-stakeholder", "feat.f3": "Every comment is tagged by environment, stakeholder, and author.",
      "feat.f4t": "Element + source aware", "feat.f4": "Captures the selector, snapshot, the CSS that wins, the page route, and an optional source path.",
      "feat.f5t": "AI-agnostic", "feat.f5": "Any AI tool applies the changes with plain HTTP — Claude Code, Cursor, and more.",
      "feat.f6t": "Style-isolated", "feat.f6": "The widget renders in a Shadow DOM, so it never clashes with your app's CSS.",
      "feat.f7t": "A real dashboard", "feat.f7": "Triage, statuses, roles, and per-project views — for the whole team.",
      "feat.f8t": "Multi-tenant", "feat.f8": "Each workspace sees only its own data, enforced server-side.",
      "team.stkT": "For stakeholders", "team.stk": "Point and comment on the live app — no install, no setup. Clients, PMs, and testers just click.",
      "team.devT": "For developers", "team.dev": "Pull the queue and let any AI apply the changes to the real source. Comments carry the page route and the CSS that actually wins.",
      "final.title": "Try it in one click — no install", "final.demo": "Try the demo", "final.signup": "Create an account",
      "foot.dashboard": "Dashboard", "foot.docs": "Docs",
      "anim.label": "Make this bolder"
    },
    ar: {
      "nav.how": "كيف يعمل", "nav.features": "المميزات", "nav.demo": "تجربة",
      "nav.signin": "تسجيل الدخول", "nav.demoCta": "جرّب العرض",
      "hero.title": "أشِر إلى الواجهة. ونفّذ بالذكاء الاصطناعي.",
      "hero.sub": "أي عضو في فريقك ينقر على عنصر في التطبيق الفعلي ويترك تعليقًا قصيرًا. ثم يسلّم المطوّر قائمة الملاحظات لأي أداة ذكاء اصطناعي لتطبّق التغيير على الكود المصدري مباشرة — دون ترجمة ملاحظات غامضة إلى كود.",
      "hero.demo": "جرّب العرض — بدون تثبيت", "hero.signup": "أنشئ حسابًا",
      "contrast.badTitle": "بدون Pointer", "contrast.bad": "«اذهب إلى صفحة الدفع، وابحث عن العنوان، واجعل حجمه ٢٤ بكسل وأغمق قليلًا…»",
      "contrast.goodTitle": "مع Pointer", "contrast.good": "🐕 انقر العنوان ← 💬 «اجعله ٢٤ بكسل» ← ✨ يطبّقه الذكاء الاصطناعي.",
      "how.title": "كيف يعمل",
      "how.s1t": "انقر وعلّق", "how.s1": "أي مستخدم مسجّل ينقر على أي عنصر في التطبيق ويترك تعليقًا قصيرًا.",
      "how.s2t": "تُجمع وتُصنّف", "how.s2": "تُحفظ التعليقات لكل مشروع وتُوسم بالبيئة والجهة المعنية والكاتب.",
      "how.s3t": "يطبّقها الذكاء الاصطناعي", "how.s3": "يسلّم المطوّر القائمة لأي أداة ذكاء اصطناعي فتعدّل ملفات الكود الفعلية.",
      "feat.title": "مصمّم للفرق الحقيقية",
      "feat.f1t": "تركيب بسطرين", "feat.f1": "أضِف وسم سكربت ووسم العنصر. بدون حزمة وبدون SDK.",
      "feat.f2t": "متعدد المشاريع", "feat.f2": "خادم واحد يخدم عدة تطبيقات، مقسّمة حسب المشروع.",
      "feat.f3t": "متعدد الجهات", "feat.f3": "كل تعليق موسوم بالبيئة والجهة المعنية والكاتب.",
      "feat.f4t": "مدرك للعنصر والمصدر", "feat.f4": "يلتقط المحدِّد واللقطة وقواعد CSS الفاعلة ومسار الصفحة ومسار المصدر اختياريًا.",
      "feat.f5t": "محايد تجاه الأدوات", "feat.f5": "أي أداة ذكاء اصطناعي تطبّق التغييرات عبر HTTP — Claude Code وCursor وغيرها.",
      "feat.f6t": "معزول الأنماط", "feat.f6": "تُعرض الأداة داخل Shadow DOM فلا تتعارض مع تنسيقات تطبيقك.",
      "feat.f7t": "لوحة تحكم حقيقية", "feat.f7": "فرز وحالات وأدوار وعروض لكل مشروع — لكل الفريق.",
      "feat.f8t": "متعدد المستأجرين", "feat.f8": "كل مساحة عمل ترى بياناتها فقط، مع فرض ذلك من الخادم.",
      "team.stkT": "للجهات المعنية", "team.stk": "أشِر وعلّق على التطبيق الحي — بدون تثبيت أو إعداد. العملاء ومديرو المنتج والمختبِرون ينقرون فقط.",
      "team.devT": "للمطوّرين", "team.dev": "اسحب القائمة ودع أي ذكاء اصطناعي يطبّق التغييرات على الكود الفعلي. تحمل التعليقات مسار الصفحة وقواعد CSS الفاعلة.",
      "final.title": "جرّبه بنقرة واحدة — بدون تثبيت", "final.demo": "جرّب العرض", "final.signup": "أنشئ حسابًا",
      "foot.dashboard": "لوحة التحكم", "foot.docs": "الوثائق",
      "anim.label": "اجعله أعرض"
    }
  };

  function applyLang(lang) {
    var dict = STRINGS[lang] || STRINGS.en;
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var k = el.getAttribute('data-i18n');
      if (dict[k] != null) el.textContent = dict[k];
    });
    document.documentElement.lang = lang;
    document.documentElement.dir = (lang === 'ar') ? 'rtl' : 'ltr';
    var toggle = document.getElementById('lang-toggle');
    if (toggle) toggle.textContent = (lang === 'ar') ? 'English' : 'العربية';
    try { localStorage.setItem('pointer_lang', lang); } catch (e) {}
  }

  (function initLang() {
    var saved = null;
    try { saved = localStorage.getItem('pointer_lang'); } catch (e) {}
    var lang = saved || ((navigator.language || '').toLowerCase().indexOf('ar') === 0 ? 'ar' : 'en');
    applyLang(lang);
    var toggle = document.getElementById('lang-toggle');
    if (toggle) toggle.addEventListener('click', function () {
      applyLang(document.documentElement.lang === 'ar' ? 'en' : 'ar');
    });
  })();
</script>
```

- [ ] **Step 2: Add RTL adjustments to the `<style>` block**

```css
[dir="rtl"] .nav-links { margin-inline-start: 0; margin-inline-end: auto; }
[dir="rtl"] .step .num { }
[dir="rtl"] .pf-pop, [dir="rtl"] .pf-pin, [dir="rtl"] .pf-ring, [dir="rtl"] .pf-cursor { /* animation stage stays LTR (it mimics an app UI) */ }
[dir="rtl"] .hero-stage { direction: ltr; }
```

- [ ] **Step 3: Verify every `data-i18n` key exists in both dictionaries**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api/landing
python3 - <<'PY'
import re, json
html = open('index.html', encoding='utf-8').read()
keys = set(re.findall(r'data-i18n="([^"]+)"', html))
# crude extraction of the two dicts' keys
en = set(re.findall(r'"([a-zA-Z.]+)":', html.split('en: {')[1].split('ar: {')[0]))
ar = set(re.findall(r'"([a-zA-Z.]+)":', html.split('ar: {')[1]))
missing_en = keys - en
missing_ar = keys - ar
print("keys in markup:", len(keys))
print("missing in en:", sorted(missing_en))
print("missing in ar:", sorted(missing_ar))
PY
```
Expected: `missing in en: []` and `missing in ar: []`.

- [ ] **Step 4: Verify the toggle flips dir/lang in a browser**

Serve and open in the browser worker. Click `#lang-toggle`; confirm `<html dir>` becomes `rtl`, Arabic text appears, the toggle label reads `English`, and `localStorage.pointer_lang === 'ar'`. Toggle back and confirm `ltr`/English.

- [ ] **Step 5: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html
git commit -m "feat(landing): bilingual en/ar i18n, toggle, RTL, persistence"
```

---

## Task 6: Responsive + cross-browser QA pass

**Files:**
- Modify: `pointer-api/landing/index.html` (only if QA finds issues)

**Interfaces:** none new — this is a verification gate over Tasks 1–5.

- [ ] **Step 1: No horizontal scroll at mobile width**

Serve; in the browser worker set viewport to 375×800 and confirm `document.documentElement.scrollWidth <= window.innerWidth` (no sideways scroll), in both `ltr` and `rtl`.

- [ ] **Step 2: CTAs are reachable and correct at mobile width**

At 375px confirm both hero CTAs and both final-band CTAs are visible and tappable, and their `href`s are the demo/app hosts.

- [ ] **Step 3: Lighthouse-ish sanity (optional, if browser worker supports it)**

Confirm no console errors, images have non-empty `alt` or `alt=""` (decorative), and the page has a single `<h1>`.

- [ ] **Step 4: Fix any issues inline, then commit (skip commit if nothing changed)**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add landing/index.html && git commit -m "fix(landing): responsive/RTL QA adjustments" || echo "no changes"
```

---

## Task 7: Caddy block + deploy wiring (NO deploy yet)

**Files:**
- Modify: `pointer-api/Caddyfile`
- Modify: `pointer-api/docker-compose.prod.yml`
- Modify: `pointer-api/DEPLOY.md`

**Interfaces:**
- Consumes: `landing/` from Tasks 1–6.
- Produces: a `pointer.moamen.work` static-server block, a `./landing:/srv/landing:ro` bind mount on the Caddy service (mirroring `./dashboard:/srv/dashboard:ro`), and a documented deploy step. The VM reload happens only at the user-approved deploy.

Context (confirmed): the Caddy service in `docker-compose.prod.yml` bind-mounts the repo's `dashboard/` dir read-only at `/srv/dashboard` and serves it. We mirror that for `landing/` → `/srv/landing` — **no copy step**, Caddy serves the repo dir directly, so a `git pull` updates the live files.

- [ ] **Step 1: Replace the redirect block in `Caddyfile`**

Find:
```
pointer.moamen.work {
    redir https://app.pointer.moamen.work{uri} permanent
}
```
Replace with:
```
# Bare domain → the marketing landing page (served from the bind-mounted landing/ dir).
pointer.moamen.work {
    root * /srv/landing
    encode gzip
    try_files {path} /index.html
    file_server
}
```

- [ ] **Step 2: Add the bind mount to the Caddy service in `docker-compose.prod.yml`**

Find the Caddy `volumes:` list (it contains `- ./dashboard:/srv/dashboard:ro`) and add, right after that line:
```yaml
      - ./landing:/srv/landing:ro
```

- [ ] **Step 3: Add the landing deploy step to `DEPLOY.md` (under "Updating")**

```markdown
**Landing page change** — from your machine `git push origin main`, then on the VM:

\`\`\`bash
cd ~/pointer-api && git pull --ff-only        # updates ./landing (bind-mounted into Caddy)
docker compose -f docker-compose.prod.yml up -d --force-recreate caddy
# force-recreate so the new Caddyfile + the landing bind-mount are picked up (single-file
# bind-mount inode gotcha).
\`\`\`
```

- [ ] **Step 4: Validate the Caddyfile parses (local, optional)**

Run (skip if docker/caddy unavailable; the VM start will validate either way):
```bash
cd /Users/momen/Desktop/REPOS/pointer-api && docker run --rm -v "$PWD/Caddyfile":/etc/caddy/Caddyfile caddy:2 caddy validate --config /etc/caddy/Caddyfile 2>&1 | tail -3 || echo "skip"
```
Expected: "Valid configuration" (or skip).

- [ ] **Step 5: Verify the edits are in place**

Run:
```bash
cd /Users/momen/Desktop/REPOS/pointer-api
grep -c 'root \* /srv/landing' Caddyfile
grep -c './landing:/srv/landing:ro' docker-compose.prod.yml
grep -c 'redir https://app.pointer.moamen.work' Caddyfile   # expect 0 — redirect removed
```
Expected: `1`, `1`, `0`.

- [ ] **Step 6: Commit**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
git add Caddyfile DEPLOY.md docker-compose.prod.yml
git commit -m "feat(landing): serve pointer.moamen.work as the landing page (Caddy bind-mount + deploy docs)"
```

---

## Task 8: Netlify decommission — remove files + correct docs (`Pointer` repo)

**Files (in `/Users/momen/Desktop/REPOS/Pointer`):**
- Delete: `netlify.toml`, `netlify/functions/api.mjs` (+ `netlify/`), `package.json`, `public/index.html`
- Modify: `.gitignore`, `README.md`, `AGENTS.md`, `CLAUDE.md`

**Interfaces:** none — independent cleanup. Safe because no live app points `VITE_POINTER_SERVER` at `tuwaiq-pointer.netlify.app` (user confirmed). Keeps `comments-skill/` (local zero-dep server) intact.

- [ ] **Step 1: Confirm nothing else references the deleted pieces**

Run:
```bash
cd /Users/momen/Desktop/REPOS/Pointer
grep -rIn "tuwaiq-pointer.netlify.app\|@netlify/blobs\|netlify/functions" . --exclude-dir=.git --exclude-dir=node_modules
```
Expected: only matches inside the files we're about to delete or the docs we're about to edit. If a `comments-skill/*` or `test.html` references the Netlify URL, note it for Step 3.

- [ ] **Step 2: Delete the Netlify files**

```bash
cd /Users/momen/Desktop/REPOS/Pointer
git rm -r netlify netlify.toml package.json public/index.html
# remove public/ entirely only if it has no other source files:
rmdir public 2>/dev/null || true
```
Expected: files staged for deletion.

- [ ] **Step 3: Remove the `.netlify/` line from `.gitignore`**

Edit `.gitignore` and delete the line `.netlify/` (leave the rest untouched).

- [ ] **Step 4: Correct the docs**

In `README.md`, `AGENTS.md`, and `CLAUDE.md`, remove/replace every mention of the Netlify backend and the `tuwaiq-pointer.netlify.app` URL so they describe only:
- the live SaaS on the Oracle VM — `api.pointer.moamen.work`, `app.pointer.moamen.work`, `demo.pointer.moamen.work` (and `pointer.moamen.work` = the landing page), and
- the local zero-dependency `node server.js` (in `comments-skill/`) for self-hosting.

Concretely:
- `README.md` Quick start: change the server URL examples from `https://tuwaiq-pointer.netlify.app` to `https://api.pointer.moamen.work`; in the hosting/"Local (solo)" section, drop the "point at the netlify URL" line and keep the local-server + tunnel guidance.
- `AGENTS.md`: delete the "Repo root — Netlify deployment wrapper" bullet, the `netlify/functions/api.mjs` bullets, the `npm install @netlify/blobs` build note, the "Netlify Blobs (deployed)" storage line, and the "Module format split" note's Netlify clause. Update the "Reconciling with CLAUDE.md" section to state the deployed backend is the .NET API on the VM, not Netlify.
- `CLAUDE.md`: this file describes the legacy `comments-skill` server design; remove any `GOOGLE_SCRIPT_URL`/Netlify-deploy claims that are now false and point readers to the SaaS as the deployed product. (Keep the local zero-dep server description.)

- [ ] **Step 5: Verify no stale references remain**

Run:
```bash
cd /Users/momen/Desktop/REPOS/Pointer
grep -rIn "tuwaiq-pointer.netlify.app\|@netlify/blobs\|netlify/functions\|Netlify Blobs" . --exclude-dir=.git --exclude-dir=node_modules || echo "clean"
```
Expected: `clean` (no matches).

- [ ] **Step 6: Commit**

```bash
cd /Users/momen/Desktop/REPOS/Pointer
git add -A
git commit -m "chore: decommission Netlify backend; docs describe the VM SaaS + local server"
```

---

## Deploy (single, user-approved — do NOT run until the user says go)

1. **Landing (pointer-api):** `git push origin main`; on the VM `cd ~/pointer-api && git pull --ff-only` (updates the bind-mounted `./landing`), then `docker compose -f docker-compose.prod.yml up -d --force-recreate caddy` (force-recreate so the new `Caddyfile` + the `landing` bind-mount are picked up — single-file bind-mount inode gotcha).
2. **Netlify decommission (Pointer):** `git push origin main` (and, if desired, delete the Netlify site from the Netlify dashboard — manual, outside this repo).
3. **Live verify:**
   - `curl -sI https://pointer.moamen.work/` → `200` and serves the landing page (not a 301 to the app).
   - `curl -s https://pointer.moamen.work/ | grep -c 'demo.pointer.moamen.work'` → ≥1.
   - `curl -sI https://app.pointer.moamen.work/` and `https://demo.pointer.moamen.work/` → still `200` (unchanged).
   - In a browser: language toggle flips en↔ar/RTL, animation runs, reduced-motion shows a static frame, CTAs navigate to demo/app.

## Out of scope

Pricing/billing (Phase 3), blog/CMS, analytics, A/B testing, a real embedded live widget (the scripted animation stands in), and any change to the app or demo dashboards.
