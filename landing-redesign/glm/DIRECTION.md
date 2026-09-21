# DIRECTION — glm build: "THE PLOT"

Phase 2 builds sections 3–15 into the `TODO:PHASE2` placeholders in `index.html`. Assets already exist: `styles.css` (reuse atoms: `.btn`, `.chip`, `.kicker`, `.panel`, `.section`, `.section-title`), `main.js` (extend the `data-i18n` + `STRINGS` en/ar table and the `[data-app-link]`/`[data-demo-link]`/`[data-docs-link]` branding hooks), self-hosted `fonts/` + `fonts.css`, `favicon.svg` — don't re-fetch fonts.

**Thesis.** Pointer *plots* feedback onto exact coordinates of a running UI. Visual language: the surveyor's field sheet — warm paper, ink, crosshair reticles, mono coordinate labels, dashed dimension rails, flat stamped panels. One accent, one job: attention (marks, links, primary CTA, focus).

**Palette (WCAG-checked)** — ground `#F4F0E6`/`#161512` (light/dark) · ink `#1B1B18`/`#ECE7DA` (text + 1.5px borders) · panel `#FCFAF4`/`#1D1B17` · accent `#C8401A`/`#FF7A4A` (CTA fill; light CTA text `#FFFFFF`, dark CTA text = ground) · accent-deep `#B93A12` (accent-as-text on light ground) · steel `#4E5D68`/`#A3B4BF` (second tier: cheaper-model bar, translation chips, secondary labels) · hairline `#DCD5C3`/`#2E2B25`. State hues, never re-branded: Live `#1E7B4F`/`#4CC38A`, Ready `#946200`/`#E0A938`, Archived `#59636E`/`#8B98A5`, diff −`#B3261E` +`#1E7B4F` on tinted rows (`#F9E9E5`/`#E7F1EB` light, `#2A1A17`/`#16241C` dark), always ink text. Branding `primaryColor` overrides `--accent` (dark via `color-mix(in srgb, var(--accent) 70%, white)`).

**Type.** Archivo Black — display, all-caps, line-height 0.95, tracking −0.01em. Archivo 400/500/600 — body 17px/1.6. Fragment Mono — kickers, coordinates, code: 11–12px, tracking 0.14em, uppercase. Noto Kufi Arabic 400/500/700 — the whole Arabic voice (`:lang(ar)` display → Kufi 700; no caps or tracking in Arabic).

**Shape & motion.** Radius 0 (2px small controls). Signature: 12px 45° notch on the inline-end top corner of `.panel` (clip-path, mirrored under `[dir="rtl"]`). Dashed rails with end ticks connect related artifacts. Motion "plot & stamp": 160–280ms `cubic-bezier(.2,.8,.2,1)`; buttons stamp on hover (`translate(-2px,-2px)` + `4px 4px 0` ink). Static end-state is the CSS default; JS only adds `[data-anim]`; `prefers-reduced-motion` removes all motion.

**Scale.** 4px base; gaps 8/16/24/40; section padding `clamp(72px,12vw,128px)`; container 1200px, inline padding `clamp(20px,5vw,48px)`; display `clamp(44px,9vw,92px)`; h2 `clamp(30px,5vw,52px)`; each section opens with a numbered mono kicker (`03 · THE BRIEF`).

**Hero (already built — keep consistent).** Grid 5/7, stacked ≤960px. Copy: kicker, "A comment becomes a change.", sub, two CTAs, mono proof line. Stage (aria-hidden): three notched panels staircasing down a dashed rail — `01 · POINT` (app mock, reticle pinned on the Upgrade button, Arabic comment + `ar → en` chip + translation), `02 · BRIEF` (mono dl: selector / applied css / route / viewport / source), `03 · CHANGE` (diff `btn-sm → btn-lg` + "committed, never pushed"). Loop: pin → translate → print → stamp → the mock button grows.

**§7 cost graph.** Two columns: A uniform accent, full height; B shorter, segmented — top 62% accent ("investigation + review"), bottom 22% steel ("mechanical edits + translation"). Qualitative labels only, never numbers. Bars fill 600ms via IntersectionObserver; static end-state default; mirrors under RTL.

**Translation motif (page-wide).** Original line, steel mono chip (`ar → en` / `en → ar`), translated line beneath — hero, §4 loop, §11 alike.

**No:** blue, rounded-SaaS look, resting shadows, mascot, fabricated numbers (BRIEF.md forbidden list applies page-wide).
