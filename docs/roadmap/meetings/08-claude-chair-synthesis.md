# Claude — chair's synthesis after rounds 1–7 (proposed final; last call for objections)

## A. Settled (all three agree, or the dissenter conceded)

| # | Resolution |
|---|---|
| S1 | Tenants exist (`TenantService.cs`). §19 shrinks to "add fields when a feature needs them"; **every new table is born with `OwnerId`**. Not top-15. |
| S2 | Entitlements exist and `MaxCommentsPerMonth` is enforced (`PlanEntitlements.cs:18-36`, `CommentService.cs:89-100`). **§20 dissolves** into feature-attached wiring: email → `EmailsPerMonth`, retention job → `RetentionDays`, §35 → `MaxEnvironments`. |
| S3 | Two environment concepts (`EnvironmentTag` enum vs `AppEnvironment`/`ProjectAppUrl`). §35 = days; **gate unchanged** (needs §42 repo mapping + §9 PR to pay off; a preview comment already works today as a Staging comment with a captured URL). |
| S4 | Project list/create is plain `[Authorize]` (`Admin/ProjectsController.cs:11-17`) — §1 needs **no new project endpoints**. |
| S5 | **On-disk contract freeze before `init` ships** (GLM's NEW-1). Rename in the plan: `data-pf` → `data-component-source`; `data-pf-build` → `data-build-sha`; `data-pf-mask` → `data-snapshot-mask`. Everything else in GLM's table: keep as-is, dual-read after the rebrand, permanent npm deprecate-stub. |
| S6 | §17 injection regex → **cut**; replaced by a secrets/payload flag (`sk-`, `AKIA`, `ghp_`, long base64, `<script>`, `curl … \| sh`), advisory only, never in the apply payload. |
| S7 | Email: **hold**; in-app first, webhooks (§32) before email; trigger = first non-dev stakeholder outside the founder's workspace, or comment→author-revisit p50 > 24 h. Wiring is small because `EmailsPerMonth` already exists. |
| S8 | §24 MCP ships **in the same release as §7 apply core**, inside the same npm package (`npx pointer mcp`). `.mcp.json` documented as user-level config, not repo-committed. |
| S9 | §1 widget injection: **Vite + static deterministic; Next/App-Router + monorepos routed to the AI skill** and said so in the CLI output. |
| S10 | §41 (allowed origins via `ProjectAppUrl` + comment-POST rate limit): threat is "authenticated flood", not anonymous. **Release 1, alongside §1** (a day of work; the funnel widens the commenter population the same week). |
| S11 | §21 usage events (`installed`, `first_comment`, `first_apply`, `apply_failed`) ship **inside §1**, tenant-scoped from birth. |
| S12 | §28 auto re-screenshot → redesign as manual attach; §23 fake terminal → cut into §47 seeded demo; §11 batch-by-file → after Phase 4; §31 deploy awareness → with Phase 4 (staging first, `CommitSha` column). |
| S13 | Widget failure behaviour documented (agy r2 Q3): init network/5xx **silent**; submit failure → toast; 401 → login modal; project disabled → silent teardown. §40 kill switch = the existing `disableSilently()` path driven by a per-project flag — small. |
| S14 | Adopt from agy's "missed" list: **dashboard blast-radius column per item** (Orval regen + UI in the separate Angular repo); **additive-only EF migrations while self-hosters exist**; **tenant-vs-client query-filter rule** attached to §44; **CSP note** (constructed `CSSStyleSheet` for nonce-strict hosts; scripts need host allowlisting) folded into NEW-3. |

## B. Chair's rulings on the three open disputes

### D1 — API keys (agy: full §25 before §1 · GLM: hash-only before MCP, full §25 later)
**Ruling: NEW-5 "key hardening" in Release 1, order-independent of §1; full §25 UI later.**
- Facts: `User.ApiKey` is plaintext and compared by SQL equality (`AuthService.cs:213`); keys already have a `ptr_` prefix (`install.sh:56`).
- Hardening is **server-side only** — the key string the user holds does not change format, so nothing `init` writes to `.pointer/credentials.env` is affected. agy's "must precede init" therefore doesn't hold; but it should land in the same release because `init` multiplies the number of keys on disks.
- Scope of NEW-5: new `ApiKey` table (`UserId`, `Prefix` = first 12 chars indexed, `Hash` = SHA-256 of the full key, `Scopes` flags with default `Full`, `LastUsedAt`, `RevokedAt`, `OwnerId`); migration hashes existing `User.ApiKey` rows into it and drops the column; `login-with-key` looks up by prefix and verifies the hash; `MeController` `api-key`/`regenerate` keep working (one active key per user for now). No dashboard UI beyond what exists.
- Full §25 (multiple named keys, scope picker, per-project restriction, dashboard list/revoke) keeps its hold trigger: first key in CI or a committed config.

### D2 — Phase 4 approach (agy: hold / use fiber `__source` · GLM: Vite transform, opt-in)
**Ruling: GLM's design, opt-in first.**
- agy's fiber-`__source` route is **dev-mode only** — the widget already does exactly that as tier 2 (`framework-source.ts`, `fiber._debugSource`). Phase 4 exists because production strips it. So the recommendation solves the wrong problem.
- A Vite transform adding a `data-*` attribute is compile-time and inert; it cannot break rendering. Fragments: stamp **each top-level host element** the component returns (a Fragment has several roots — stamp all); a component that returns only other components has no host element → miss, and the ancestor walk (`capture.ts:246-254`) finds the nearest stamped ancestor. HOCs stamp the wrapped component's root.
- Ship **opt-in behind a flag** (`pointer init --source-map` / plugin option), default-on after hash stability is proven on 2–3 real apps. Vite first; Angular builder and Next RSC stay effort-flagged (weeks each).
- Reuse check: LocatorJS / `vite-plugin-react-inspector` / `click-to-component` are dev-mode fiber readers — same limitation; not reusable for the prod goal. The transform itself is small (Babel/SWC visitor on JSX roots); the manifest writer is the actual code.

### D3 — §18 scoping rules in top 15 (agy: yes · GLM + Claude: hold)
**Ruling: hold.** Zero clients today; "Client can't target /admin" is speculative until a real client relationship defines "scope". First-customer trust comes from §33-lite, audit log (§16), and key hardening. Trigger: first Client-role commenter on a production environment.

## C. Proposed final top 15 (by release)

**Release 1 — "install without an AI"**
1. NEW-1 on-disk contract freeze (decision doc, ½ day)
2. §1 `npx pointer init` (+ §6 doc fix, §21 events, existing endpoints) — 1–2 w
3. §2 pre-filled `--key` command in dashboard quick-start — 1 d
4. §3+§4 `doctor` + `GET /api/meta` — 2–3 d
5. §41 allowed origins + comment rate limit — 1 d
6. NEW-5 API-key hardening (hash + prefix + table) — 2–3 d
7. NEW-4 continuous verification: schedule existing `e2e/` + fresh-app init scenario + white-label job — 2–3 d

**Release 2 — "apply from anywhere"**
8. §7 apply core lib + CLI (`--plan` rides along) — 1–2 w
9. §24 MCP server, same package — 1–2 w
10. NEW-2 served-file version stamp + `pointer update` — ½ d
11. §10 in-app author notification with commit link — 2–3 d
12. §13 quick-access invites, link-copy delivery — 2–3 d

**Release 3 — "apply quality in production"**
13. Phase 4 Vite plugin + manifest, opt-in, stamping `data-component-source` — 1–2 w (+ §31 `CommitSha` column, staging-first)
14. §45 design tokens into `stack.json` — 1–2 d
15. NEW-3 widget release engineering (immutable `?v=`, `stable`, SRI, ≤ 60 KB gz budget, CSP note) + §33-lite snapshot privacy — 3–5 d

**Hold list** as in GLM round 3, with deltas: §20 dissolved; §35 re-estimated to days (gate unchanged); §25 full → trigger "first key in CI / committed config"; §18 held.

## D. Last call
Answer per item: ACCEPT, or OBJECT with a one-line reason and the concrete alternative. Anything not objected to is final.
