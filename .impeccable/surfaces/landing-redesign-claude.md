---
version: 1
slug: "landing-redesign-claude"
primary_target: "landing-redesign/claude"
related_targets: []
---

## Direction contract

THESIS: Pointer's landing page is a pull-request review, not a marketing page — every section reads
like a diff view (state hues, hairlines, monospace metadata) instead of the generic gradient-hero
SaaS template every AI-tool landing page defaults to.

OWN-WORLD: "The Review Margin" — white canvas (#fff / dark #0d1117), cool gutter (#f6f8fa / #161b22),
hairline borders (#d0d7de), brand blue #0969da used ONLY for links/focus/primary-button (four-jobs
rule), fixed state hues (Open/Ready/Applied/Archived/Danger) independent of brand color, IBM Plex
Sans Arabic for text + IBM Plex Mono for code/keys/counts, 6px radii, flat at rest, shadows only on
floating layers.

STORY: A visitor understands in one scroll that a comment is not a screenshot — it's a structured
brief an AI can act on directly — and that the product is honest about what it saves (qualitative,
never a fabricated percentage).

FIRST VIEWPORT: A diff-styled hero: left side the one-sentence promise + two CTAs (Try the demo /
Create an account); right side a live-feeling "comment → structured brief → applied diff" mini
sequence rendered as an actual PR-review-style card stack (selector, snapshot, appliedCssRules
visibly present as monospace fields), not a static screenshot. Primary CTA sits above the fold at
360px.

FORM: Pinned brief-committed world ("The Review Margin"), no concept-seed roll run — the direction
was pinned by the product owner in `docs/roadmap/prd/LANDING-DESIGN-PROMPT.md` and confirmed in
`landing-redesign/BRIEF.md`; per new-work.md, a brief-pinned world beats the roll, always. Build path:
code-led (unattended multi-agent background session, no image-generation round run this session).

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the
verdict, DESIGN.md, and every shipping raster carrying its provenance.
