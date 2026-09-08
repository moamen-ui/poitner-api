# Review response — what was accepted, what was rejected, and why

Every finding from every review round, with the verification I did before acting on it. A finding
that was confidently stated and turned out to be wrong is recorded as such — that is the point of
keeping this file.

**Rule applied throughout:** no finding was accepted on the reviewer's authority. Each was checked
against the actual code first, and the plan cites the file:line I verified, not the reviewer's claim.

---

## Round 1 — Antigravity CLI (`gemini-3.1-pro-high`) → `REVIEW-AGY.md`

Shell access was denied in its headless run (see that file's provenance note), so it reviewed by
opening files and checking cited line numbers — which is exactly what I most needed checked.

| # | Finding | Verified? | Action |
|---|---|---|---|
| 1 | The plan's cited paths and line numbers are accurate across `Program.cs`, `AuthenticationExtensions.cs`, `ProfileService.cs`, the served skills, the dashboard storage layers, and the widget | Confirmed by spot-check | No change — but this was the main thing I wanted independently checked |
| 2 | `poitner` typo also lives in `BrandingService.cs:15` (`DefaultUrlDocs`) | **Yes** — verified; it is a **customer-facing docs URL** | **Accepted.** Added to §5.11 and cited in §7.3 set 1 and §8.2 |
| 3 | `Program.cs` rewrites a `<POINTER_SERVER>` placeholder in served skill files | **Yes** — `injectedFiles` HashSet at `Program.cs:197-201`, `.Replace()` at `:212` | **Accepted, and escalated.** This is a four-way coupling (filenames + HashSet + placeholder token + Replace call); miss one and users get a literal `<POINTER_SERVER>` with a 200 OK. Added to §5.3, §8.4 (with a curl-grep gate), §9, and R19 |
| 4 | `poitner` typo is in `orval.config.ts` | **No — checked, it is not there** | **Rejected.** Recorded in `REVIEW-AGY.md` so the wrong claim doesn't propagate |
| 5 | Changing `JWT__Issuer` invalidates all live sessions | **Yes** | Already in the plan (§4.6) — independent confirmation of the plan's highest-severity item |
| 6 | Storage-key renames silently log everyone out | Yes | Already §4.3 |
| 7 | `ptr_` prefix needs backwards compatibility or deployed agents lock out | Yes | Already §5.11 / Q27 |
| 8 | npm packages must be republished, not renamed | Yes | Already §8.6 |
| 9 | DB name/role rename is manual DB ops, not EF migrations | Yes | Already §10.4 |

**What it did not find:** the `pf-` / `--pf-*` surface (175 classes, and a public theming API), the
CORS allow-list, the static-file cache filename check, and the non-code half of the brand (Branding
rows + artwork). Those came from my own audit and the round-3 mining below.

---

## Round 2 — GLM-5.3 (`opencode`) → `REVIEW-GLM.md`

*(filled in below when the run completed — see that section)*

---

## Round 3 — mined from an existing review of a *different* plan

While writing this, a parallel effort produced `docs/REBRANDING_MASTER_PLAN.md` plus two reviews of
it (`docs/reviews/CLAUDE_REBRANDING_REVIEW.md`, `docs/reviews/GLM_REBRANDING_REVIEW.md`). Those
reviews are about a different document, but they were verified against **this same codebase**, so
their factual findings transfer. I re-verified each one myself before using it.

| # | Finding (source) | Verified? | Action |
|---|---|---|---|
| 1 | The plan is unaware of the **runtime Branding system** (Claude review §1.1) | **Yes** — `BrandingService.cs:10,69`, `BrandingResponse.cs:13,15` (`Logo`/`Favicon`), `GET /api/branding`, per-tenant rows, and `uploads/` holding two tenants | **Accepted, promoted to a headline concept.** New §3.1: half the user-visible brand is DB rows + uploaded artwork, so editing `DefaultProductName` renames nothing on a deployed instance. Referenced from the rules of engagement |
| 2 | `/embed.js` is **generated inline** in `Program.cs:283-319` and hardcodes the tag, the `pointer.js` path, and `window.__pointerEmbedded` | **Yes** | **Accepted.** My §5.3 had wrongly listed `embed.js` as a static `wwwroot` asset — corrected |
| 3 | The `Pointer` **config section** exists in three coupled places | **Yes** — `appsettings.json:9-14`, `GetSection("Pointer")` at `Program.cs:180`, `Pointer__*` in `docker-compose.prod.yml:38-41` | **Accepted.** New §4.7 — renaming one silently returns `Enabled=false` and disables the Swagger embed |
| 4 | Hardcoded **CORS** dashboard origins | **Yes** — `Program.cs:61-68`, five `*.pointer.moamen.work` entries gating `/api/admin/*` | **Accepted.** §8.2 + R18 |
| 5 | Static-file cache special-case matches the widget **by filename** | **Yes** — `Program.cs:250-252` | **Accepted.** §5.3 + §8.2 — renaming the assets without it means shipped widget fixes stop reaching customers |
| 6 | `.env.example` (not just `.env.prod.example`) carries `Database=pointer`, `JWT__Issuer=pointer-api`, `admin@pointer.local` | **Yes** | **Accepted.** §8.2, §5.11 |
| 7 | Extension postMessage discriminators: `source: 'pointer-ext'`, `'pointer-ext-res'`, `PROXY_TOKEN='__pointer_via_proxy__'` | **Yes** — `inject-main.ts:20,29,52,64`, `content-bridge.ts:22`, `background.ts:180,269` | **Accepted, and escalated:** Chrome Web Store rollout is **not** atomic with a server deploy, so an old extension will talk to a new server for days. §8.8 now requires both discriminators during rollout |
| 8 | `POINTER_SWAGGER_URL` env var, with the **old host hardcoded** in the workflow | **Yes** — `publish-clients.yml:54`, `generate-clients.mjs:24` | **Accepted.** §8.6 |
| 9 | The audit must exclude `.playwright-cli/` / `.playwright-mcp/` DOM snapshots | **Yes** — 316 + 68 brand-matching files in `pointer-api`, 189 in `pointer-dashboard` | **Accepted.** Added to the gate's `EXCLUDES` and to §7.1 |
| 10 | Dashboard builds fail if the client packages aren't published yet | Yes (by inspection of the dependency) | Already correctly ordered (§6.2 puts clients before dashboards), but I added the local-verification escape hatch (`file:` / `npm link`) and the warning never to commit it |
| 11 | Verification must be case-insensitive | Yes | **Accepted** — the gate now greps `-i` over `pointer|poitner` rather than enumerating casings |
| 12 | `web-component/src` and `extension/src` were under-specified (only 3-4 of 23 and 2 of 6 files) | **Yes** — 22 of 23 widget files and 5 of 6 extension files carry the brand | **Accepted.** Full file lists now in §8.3 and §8.8 |

---

## Round 4 — my own audit after the reviews (not raised by any reviewer)

| # | Finding | Action |
|---|---|---|
| 1 | **`pf-` is the widget's entire CSS class namespace** — 175 distinct classes, 1,020 occurrences ("pointer feedback"). It never spells "pointer", so no gate can see it | New §5.11 + Q26. Classes are shadow-DOM-encapsulated (`element.ts:192`) so renaming is internally safe |
| 2 | **`--pf-*` custom properties are a documented public theming API** — `_variables.scss:3-17` instructs consumers to write `pointer-feedback { --pf-primary: … }`, and custom properties pierce the shadow boundary | §8.3 + §9: emit `var(--<new>-x, var(--pf-x, <default>))` or every customer's theme silently reverts to defaults |
| 3 | The plan asserted **"237 tests"** and **"42 Angular tests"** as gate numbers. Actual: 309 `[Fact]/[Theory]` methods + 28 `[InlineData]` rows, and 43 `it()` blocks | Removed every hardcoded count; phase 1 now **baselines the suites** and later gates compare against that file. A plan for an autonomous agent must not assert a number it hasn't measured |
| 4 | The physical DB schema was described with **PascalCase** table names (from the `DbSet` properties). The real tables are **snake_case** (`app_settings`, `comments`, …) | §11.1 corrected — every SQL statement in the plan now uses physical names; a quoted PascalCase name would fail with `relation does not exist` |
| 5 | The screenshot URL lives **inside** `comments.element` (a JSON text column) alongside `PageUrl`, which the plan says to preserve | §11.4 rewritten: `jsonb_set` on the single key, never a column-wide `replace()`; and the preferred answer is to not rewrite at all while the old host still proxies |
| 6 | The **e2e harness is a working replica of the customer contract** (fixture apps embedding the tag + loader, a widget spec, AI-agent cases) | §5.1 + §12.3: use it as the cheapest proof, and keep one fixture on the *old* contract so the compat layer is regression-tested continuously |
| 7 | The gate script had two real bugs, found by testing it on synthetic fixtures: POSIX `awk` silently ignores `\b`, so the non-spelling residue pass never matched; and it flagged **its own filename** | Both fixed; Pass B moved into `grep`. Verified: a finished rename passes, `pf-` residue and a brand-named file both fail |
| 8 | i18n was described as "only a few literals" | Measured: **132** across six `{en,ar}.json` files, and the **Arabic** copy embeds the Latin-script brand inline in RTL sentences → new interview question Q4b (one agreed transliteration, decided once, not per-agent) |
