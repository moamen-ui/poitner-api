# Pointer Landing Page — Design

Date: 2026-06-30
Status: Approved (brainstorming complete; ready for implementation plan)

## Summary

A single-page, bilingual (en/ar) marketing landing page for Pointer, served at the
bare domain **`pointer.moamen.work`** (which today merely redirects to the app). It
explains the product to a **whole product team** and drives two CTAs: **Try the demo**
(→ `demo.pointer.moamen.work`) and **Create account / Sign in** (→ `app.pointer.moamen.work`).
The page is a self-contained static `index.html` (inline CSS + vanilla JS, no build step),
matching the product's zero-dependency identity. The hero centerpiece is a self-running,
backend-free **scripted animation** of the widget in action. As part of the same effort,
the **legacy Netlify backend is decommissioned** and the docs that reference it are
corrected.

This effort spans two repos:
- **`pointer-api`** — the landing page source + Caddy/deploy wiring (the VM pulls this repo).
- **`Pointer`** (the legacy/widget repo) — Netlify file removal + doc cleanup.

## Goals & success criteria

- `pointer.moamen.work` serves the landing page (no longer redirects to the app);
  `app.pointer` and `demo.pointer` are unchanged.
- Both CTAs work: "Try the demo" → `https://demo.pointer.moamen.work`,
  "Create account"/"Sign in" → `https://app.pointer.moamen.work`.
- The scripted hero animation runs and loops; honors `prefers-reduced-motion` with a static fallback.
- Fully responsive (mobile → desktop); no horizontal body scroll.
- Bilingual: English + Arabic, toggle in the nav, correct `dir`/`lang`, RTL layout for Arabic, choice persisted.
- Netlify backend decommissioned; `README.md`, `AGENTS.md`, `CLAUDE.md` no longer make stale Netlify claims.

## Audience & positioning

Primary audience: the **whole product team**. Core message: *everyone points at the UI;
developers ship it with AI.* The page balances the stakeholder-feedback story (anyone
clicks an element and comments, no install) with the developer-apply story (pull the
queue, hand it to any AI, it edits the real source).

## Tech & hosting

- **One self-contained `index.html`** — inline `<style>` + a small inline `<script>`;
  brand assets (dog mascot, favicon) placed alongside or inlined as data URIs. **No
  framework, no build step.** Fast and Lighthouse-friendly.
- **Source location:** `pointer-api/landing/` (next to the `Caddyfile` and deploy script).
- **Deploy:** the existing VM deploy pipeline copies `landing/` → `/srv/landing` on the VM
  (same mechanism that ships the demo dashboard to `/srv/dashboard`).
- **Caddy:** change the `pointer.moamen.work` block from a redirect-to-app to a static
  file server:
  ```
  pointer.moamen.work {
      root * /srv/landing
      encode gzip
      file_server
  }
  ```
  Caddy single-file bind-mount gotcha applies if relevant: `docker compose up -d
  --force-recreate caddy` after the Caddyfile changes (new inode).

## Page structure (top → bottom)

1. **Sticky nav** — logo + mascot; anchor links (How it works · Features · Demo);
   **language toggle (EN / ع)**; `Sign in` (→ app) + primary `Try the demo` (→ demo).
2. **Hero** — team-wide headline + subhead, the two CTAs, and the scripted animation as the visual.
3. **Problem → solution** — the contrast framing: *"Go to the checkout page, find the
   header, make the title 24px"* → *click the title → 💬 "make this 24px" → ✨ AI applies it.*
4. **How it works (3 steps)** — ① anyone clicks an element & leaves a short comment
   ② comments are collected and tagged `{ project, environment, stakeholder }`
   ③ a developer hands the queue to any AI tool, which applies the change to the real source.
5. **Features grid** — two-line install · multi-project · multi-stakeholder/environment ·
   element + source aware · AI-agnostic · Shadow-DOM style isolation · dashboard · multi-tenant.
6. **Built for the whole team** — split panel: *stakeholders* (point & comment, no install)
   vs *developers* (pull the queue, AI applies).
7. **Final CTA band** — "Try it in one click, no install" → demo + signup.
8. **Footer** — links (dashboard, docs, GitHub if public), brand, mascot.

## Visual design

Existing brand: **green→blue gradient**, dog mascot, light theme (slate `#0f172a` text on
white / soft-gradient sections), `system-ui` font stack. Generous whitespace, gradient
accents on CTAs and section dividers. Responsive via flex/grid with `max-width: 100%` media.

## Scripted animation (hero centerpiece)

Pure CSS/JS, self-running loop, **no backend, no abuse surface**. A mock "app UI" card
holds a few elements; an animated cursor glides to an element → a highlight outline
appears → a comment popover types out a short message (e.g., *"Make this bolder"*, localized)
→ a numbered pin drops. Styled to mirror the real `<pointer-feedback>` widget (toolbar,
pin, popover) in brand colors. Respects `prefers-reduced-motion`: when set, render a single
static annotated frame instead of animating.

## Bilingual (en / ar)

- A small JS i18n dictionary (`{ en: {...}, ar: {...} }`) keyed per text node; the toggle
  swaps `textContent`, sets `document.documentElement.lang` + `dir` (`rtl` for Arabic), and
  persists the choice in `localStorage`.
- RTL-aware layout: directional spacing/alignment use logical properties or flip under
  `[dir="rtl"]`.
- Arabic copy reuses the dashboards' existing Arabic terminology for consistency.
- Default language: English (with auto-detect from `navigator.language` as a nice-to-have,
  overridden by the persisted choice).

## Netlify decommission (the `Pointer` repo)

- **Remove:** `netlify.toml`, `netlify/functions/api.mjs` (and the `netlify/` dir),
  the root `package.json` (its only dependency is `@netlify/blobs`), the `public/`
  placeholder, and the `.netlify/` entry in `.gitignore`.
- **Keep:** `comments-skill/` (the local zero-dependency `node server.js`) and
  `comments-skill/core.js` — it simply loses its second (Netlify) consumer.
- **Update docs** so they no longer describe the Netlify backend or the
  `tuwaiq-pointer.netlify.app` URL: `README.md`, `AGENTS.md`, `CLAUDE.md`. They should
  describe (a) the live SaaS on the Oracle VM (`api`/`app`/`demo.pointer.moamen.work`) and
  (b) the local zero-dep server for self-hosting.
- **Precondition:** confirmed no live app still points `VITE_POINTER_SERVER` at the
  Netlify URL (user confirmed).

## Out of scope

- Pricing (billing is Phase 3, not live), blog/CMS, analytics, A/B testing.
- A real embedded live widget (we deliberately chose the scripted animation instead).
- Any change to the app or demo dashboards themselves.
