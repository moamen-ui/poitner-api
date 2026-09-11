# Opus review — Release-2 test-scenario documents (R2-00 … R2-06)

Read-only adversarial review of `docs/roadmap/testing/R2-*-tests.md` against `00-HARNESS.md`, the
matching `docs/roadmap/execution/R2-*.md` specs, and the real code. Verbatim as received; the edits
were applied in the following commit round.

## 1. COVERAGE GAPS

1.1 **R2-01 AC-1 is not proven.** "Covers" claims R2-01-01 proves *"prints a prompt containing the **verbatim** SECURITY section"* (`execution/R2-01-apply-core-cli.md:186`), but R2-01-01 asserts only a heading plus three fragments (`testing/R2-01-tests.md:26`); verbatim-ness is explicitly punted to `security-text.test.ts` under "Not covered here" (`:42`). Edit: either assert the full heading-scoped block byte-equal against `cli/src/apply/security-text.ts`, or downgrade the Covers line to "AC-1 partially (items + no-edits); verbatim → unit".

1.2 **R2-02 AC-2 covers 2 of 9 tools.** Only `pointer_get_queue`/`pointer_get_comment` results are scanned (`R2-02-tests.md:31-32`); `pointer_list_comments`, `pointer_reply`, `pointer_set_status`, `pointer_mark_applied`, `pointer_doctor` are never invoked, so "no result contains `prompt` outside `trusted` / never `hasPayloadFlag`" is unproven for them. Edit: add one row that calls all nine and deep-scans every serialized result.

1.3 **R2-02 AC-1 "documented schemas" is only a `required`-array check** (`R2-02-tests.md:30` step 3). No assertion on property types or enums (`pointer_set_status.status` ∈ open|ready|archived, `pointer_list_comments.pageSize` 1–100 — `execution/R2-02-mcp-server.md:40,46`). Edit: add `inputSchema.properties` key-set + enum comparison per tool.

1.4 **R2-02 AC-3 "identical to `pointer apply --mark`"** is reduced to a subject-string match (`R2-02-tests.md:33` step 4). The `commitUrl` and PATCH body equivalence is asserted nowhere. Edit: run `apply --mark` on a twin comment in the same repo and deep-equal the two resulting comment JSONs minus `id`/`appliedAt`.

1.5 **R2-03 AC-4's `.agents/` symlink half tests a non-product artifact.** `R2-03-tests.md:23` step 5 hand-creates `.agents/pointer.sh`. The symlinks the product actually creates are `.agents/pointer-init/SKILL.md` and `.agents/pointer-feedback/SKILL.md` → `../../$DIR/...` (`API/wwwroot/install.sh:29-34`; `execution/R1-02-cli-init.md:159-162`). Edit: replace step 5 with `test -L .agents/pointer-feedback/SKILL.md` after `update`, created by `init` itself.

1.6 **R2-04 AC-2 ("badge within 70 s *without reload*") cannot be proven by R2-04-01.** The PATCH that creates the notification is step 2, the page is opened at step 4 (`R2-04-tests.md:22`) — the badge is present at first paint and the poll observes no transition. Edit: move step 2 to after step 4's `capture-config` wait, then poll.

1.7 **R2-04 AC-2 names the *launcher* badge** (`execution/R2-04-inapp-notifications.md:76` → `templates.ts:135`, `.pf-launcher-badge`). No scenario asserts it; R2-04-01 only asserts the header `#pf-notify-badge`. Edit: add a collapsed-context assertion on `.pf-launcher-badge`.

1.8 **R2-06 AC-3's apply-queue cell is vacuous.** Comment `F` is created Open (`R2-06-tests.md:22` step 1) and never PATCHed to `ReadyToApply`, so `apply-queue?status=2` (`:24` step 4) cannot contain it. Edit: add "WA `PATCH /api/comments/{F}` `{status:2}`" before step 4 and assert `F`'s id is present in the response *and* the `payloadFlag` count is 0.

1.9 **R2-04 AC-5's `MarkAllRead` cell is weak**: `R2-04-tests.md:25` step 4 asserts QA's count is "still ≥ 1" rather than unchanged-exact. Edit: capture QA's count before step 4 and assert equality.

1.10 **R2-00 "Covers" points AC-2 at "R2-00-01/02 step 10"** (`R2-00-tests.md:7`); those scenarios have 8 steps. Edit: `step 10` → `step 8`.

## 2. FACTUAL ERRORS

2.1 **R2-00-03/04 invent the hand-off message.** `R2-00-tests.md:26,27` assert `/No deterministic injection for Angular|Next/`. R1-02 §F (`execution/R1-02-cli-init.md:147-152`) defines: `ℹ {kind} detected — automatic injection isn't supported for this stack yet.` and `{kind}` is the lowercase detection id (`angular`, `next` — `:H` table). AC-3 demands the text "exactly as R1-02 §F defines it". Edit: replace both regexes with `/^ℹ (angular|next) detected — automatic injection isn't supported for this stack yet\.$/m`.

2.2 **R2-00-03/04's file diff omits `.gitignore`.** `R1-02-cli-init.md:260` — "no files changed except `.pointer/`, skills, **`.gitignore`**". Edit: allow `.gitignore` in the permitted-diff set.

2.3 **A fresh browser context renders only the launcher.** `element.ts:161-163` sets `_collapsed` unless `sessionStorage.pointer_visible === '1'`; `renderChrome()` then emits *only* `TPL.launcher` and returns (`element.ts:650-656`). So `#pf-toggle` (R2-00-01 step 6, R2-00-06 step 3), `#pf-add`/`#pf-user` (R2-05-01 step 4) do not exist. Edit: click `widget.locator('#pf-launcher')` first, or `addInitScript` setting `pointer_visible='1'`.

2.4 **`widget.locator(':root')` matches nothing inside a shadow root** (R2-00-06 step 2, `R2-00-tests.md:29`). Edit: `await page.locator('pointer-feedback').innerText()`.

2.5 **R2-01-02 / R2-02-04 write to `src/a.txt` in a repo that has no `src/`.** `tempRepo()`'s initial commit is `README.md` only (`R2-01-tests.md:14` precondition 1); `printf … >> src/a.txt` fails. Edit: add `mkdir -p src` to `tempRepo()` or to the step.

2.6 **R2-01-03's expectations contradict its own fixture.** After R2-01-02 applies `id1`, only `id2` is still `ReadyToApply`, and `--mark all` commits `"Apply N pending <productName> comments"` over the queue (`execution/R2-01-apply-core-cli.md:115`). So the subject is `Apply 1 pending Pointer comments`, and `id1`'s `commitUrl` stays `null` from R2-01-02 — not the new github URL. Edit: seed a third ready comment `id3` in the fixture and assert `Apply 2 pending Pointer comments` over `{id2,id3}`.

2.7 **R2-01-05 asserts impossible state.** It expects `id2` `status === 2`, `appliedAt === null` (`R2-01-tests.md:30`), but the file's own order puts R2-01-03 (which applies `id2`) first (`:51`). Edit: use a fresh unapplied id (the `id3` from 2.6) in R2-01-05.

2.8 **R2-02-04 step 5 uses an undefined `id2`** (`R2-02-tests.md:33`); its preconditions declare only "one ReadyToApply comment id". Edit: create two queue comments in step 1 and name them.

2.9 **`Pointer:SkillVersion` is not a legal env-var name.** `R2-03-tests.md:23` step 3 / `:13`. ASP.NET hierarchical keys over env use `__`. Edit: `Pointer__SkillVersion=2026.09.12` (harness §4 itself writes `Cli__MinVersion`).

2.10 **`installPointerSh`'s contract is wrong.** `R2-03-tests.md:15` says `POINTER_AI_TOOL=other` is exported; `install.sh` has no such variable — the skills directory is the positional `$1` (`API/wwwroot/install.sh:10,14`). It also writes an **empty** `POINTER_API_KEY=` (`:63-68`) and leaves an existing `credentials.env` untouched (`:60-61`). Edit: `curl -fsSL $SERVER/install.sh | sh -s -- .agents` with `POINTER_SERVER/POINTER_PROJECT/POINTER_API_KEY` exported for the *invocation of `pointer.sh`*, and drop `POINTER_AI_TOOL`.

2.11 **R2-03-04 step 1 is inside the wrong window.** It requires the served stamp to be `A`, but the row declares itself to run "inside the same restart window as R2-03-03 steps 3–6" where the served stamp is `2026.09.12`, and the flake note fixes that order (`R2-03-tests.md:24,42`). Edit: move step 1 before the restart (pre-window), keep only step 2 inside.

2.12 **`preAuthWidget` is a 3-arg, non-exported local** (`e2e/widget/widget.spec.ts:41`). `R2-04-tests.md:22` step 4 calls `preAuthWidget(client)`; `R2-00-tests.md:29` calls `preAuthWidget(page, devToken, devUser)`. Edit: add a task to extract it to `e2e/widget/lib/pre-auth.ts` and use the 3-arg form everywhere.

2.13 **Fixture/port conflict on 4173.** R2-04 (`:12`) and R2-06 (`:13`) require `serve.mjs alpha 4173`; R2-05 (`:13`) requires the **smoke** page on 4173 (it is the only fixture honouring `?project=` — `fixture-app/smoke/index.html`, `widget.spec.ts:19-20`) and `run-e2e.sh:23` already serves `smoke` there. Edit: give the alpha fixture its own port in `PORTS` (e.g. 4178) and update R2-04/R2-06 URLs, or teach `alpha/index.html` the `?project=` override.

2.14 **R2-05-01's `#pf-user` assertion.** The title is `Signed in as ${displayName}` (`web-component/src/templates.ts:68`) and quick-access provisioning sets `DisplayName = emailNormalized.Split('@')[0]` (`Application/Services/Implementation/InviteService.cs:553`). Edit: assert the title contains `Signed in as qa-cl-<runId>-1`, not the full address.

2.15 **R2-05-06 step 4 asserts a behaviour a binding test forbids.** `POST /api/auth/login` carries no rate-limit attribute (`API/Controllers/AuthController.cs:16`), and `Tests/AuthRateLimitingTests.cs:22-29` (`Login_IsNotRateLimited`) exists precisely to keep it that way; R1-05 applies the `login` policy to `login-with-key` **only** and asserts it "**absent** on `Login`" (`execution/R1-05-allowed-origins-ratelimit.md:99,120,142`). The collateral check will get 200. Edit: `POST /api/auth/login-with-key { key: <keys.json.developer> }` → 429. Same correction to `R2-05-tests.md:16,50` and harness §9's "60/min/IP login" wording.

2.16 **R2-02-02 expects `commitStyle === 'separate'` without setting it.** `commitStyle: 2` is PATCHed only inside R2-01's spec (`R2-01-tests.md:16`); the MCP doc allows a separate `e2e-mcp-<runId>` project (`R2-02-tests.md:23`), which defaults to Single. Edit: add the `PATCH /api/admin/projects/{id} { commitStyle: 2 }` to R2-02's preconditions.

2.17 **R2-02-02 step 2 indexes `items[0]`** — order-dependent. Edit: `items.find(i => i.id === canaryId)`.

2.18 **R2-00-05 step 3's leak regex has no `g` flag**, so "count matches" tops out at 1. Edit: `/(?<![-\w])Pointer(?![-\w])/g`.

2.19 **Minor line refs:** `R2-00-tests.md:14` cites `BrandingService.cs:10-16`; the defaults begin at `Application/Services/Implementation/BrandingService.cs:9`. `R2-01-tests.md:31` says "sorted top-level keys" then lists `element` keys unsorted (`route, pageTitle`).

## 3. UNIMPLEMENTABLE STEPS

3.1 (=2.3) Collapsed launcher — R2-00-01 step 6, R2-00-06 step 3, R2-05-01 step 4, R2-05-04 step 1.

3.2 (=2.4) `widget.locator(':root').innerText()`.

3.3 (=2.7) R2-01-05 ordering. (=2.8) R2-02-04 `id2`. (=2.11) R2-03-04 stamp window.

3.4 (=2.12) `preAuthWidget` not importable.

3.5 **The card for a Local/Production comment is never in the widget list.** `fetchComments()` always sends `?environment=${this.environmentInt}` (`element.ts`, fetchComments), and `alpha/index.html:31` seeds `environment="staging"`. R2-06-01 creates `environment: 1` and R2-04-01 creates `environment: 3`, then assert `.pf-card[data-id=...]` without switching. Edit: insert `await widget.locator('#pf-env').selectOption('local'|'production')` before the card assertions (the pattern at `widget.spec.ts:77,96`). Note `#pf-env` renders only because `fixed-environment` is absent (`element.ts:112`) — the `environment` attribute alone does not hide it.

3.6 **`waitForResponse('**/capture-config')` registered after navigation.** `fetchCaptureConfig()` runs only inside `init()`, which `_boot` calls only when a token already exists (`element.ts:266-267`); in R2-00-01 it fires from the post-login callback (`element.ts:673`), in R2-05-01 from the invite redemption. `R2-00-tests.md:24` step 6 and `R2-05-tests.md:22` step 4 register the waiter afterwards. Edit: register before the triggering action, as `widget.spec.ts:61-62` does.

3.7 **R2-00-05 step 2 runs `init` twice in one app dir with the same `--create` name.** The second create collides (409) and no re-init semantics are defined in R1-02. Edit: second run uses `--project <key from run 1>` instead of `--create`.

3.8 **R2-02-01 step 7 "assert the child process exited"** — the pinned `connectMcp` surface (`R2-02-tests.md:14-21`) exposes no pid/exit handle. Edit: add `pid` and `exited: Promise<number>` to the helper contract.

3.9 **R2-06-02 step 3 cannot authenticate.** `pointer.sh:50` reads `POINTER_API_KEY` from env or `.pointer/credentials.env`; step 1 exports only `POINTER_SERVER`/`POINTER_PROJECT` (`R2-06-tests.md:23`). Edit: export `POINTER_API_KEY=<keys.json.wsAdmin>` too.

3.10 **Mailpit prerequisite missing.** R2-04-01 step 5 (`assertNoMail`) and R2-05-07/08 need the mailpit service, which harness §2 says is added by **R1-07**. Neither doc's Preconditions lists R1-07. Edit: add "Prerequisite doc merged: R1-07 (mailpit + `lib/mail.mjs`)".

3.11 **R2-00-07 step 3 ("run even if 05/06/08 failed")** cannot be expressed inside a spec file. Edit: make it a `run-e2e.sh` phase step executed after the whitelabel `finally`, reported by `lib/report.mjs`.

## 4. FLAKE RISKS

4.1 (=3.6) `waitForResponse` after `goto` — the response can land first and the waiter hangs to timeout.

4.2 **Toast lifetime is 2200 ms** (`element.ts:1523-1529`, `t.remove()`); R2-00-06 step 4 asserts on the `hideOverlay` toast (`element.ts:857`) with no stated timeout. Edit: `await expect(widget.locator('.pf-toast')).toHaveText(/Acme Review/, { timeout: 2000 })` immediately after the click.

4.3 **R2-05-06's stated isolation rationale is wrong** (=2.15). The poisoned bucket is `login-with-key`, i.e. every **CLI/MCP** scenario (apply, mcp, doctor), not password logins. Edit: rewrite `R2-05-tests.md:16,50` to name `login-with-key` and state that the apply/MCP phases must precede the `--429` phase.

4.4 **Port 4174 is shared by `vite preview` (R2-00-01) and `serve-dir.mjs` (R2-00-02)** under `--strictPort` (`R2-00-tests.md:17,52`), and both rows are PR-tier. Edit: state explicitly that the fresh-app stacks run sequentially (`workers: 1` / a serial phase in `run-e2e.sh`).

4.5 **Brand-window boot ordering is unstated.** `loadBranding()` is fetched once per widget boot (`element.ts:263`); a context created before the branding PUT keeps `Pointer`. Edit: add to R2-00-06/08 "create the browser context *after* the PUT in step 1".

4.6 **Cross-file state coupling without an enforcement mechanism.** R2-04-05 mutates seeded C8 (`verifiedAt`), R2-06-01 adds a flagged comment to `e2e-alpha`, R2-04-01→03 is a stateful chain. All three are noted in prose (`R2-04-tests.md:44-46`, `R2-06-tests.md:46`) but nothing orders `e2e/api/*.spec.mjs` (PR) against `e2e/widget/*` (nightly). Edit: name the phase order in `run-e2e.sh` and add the ids to harness §8's phase list.

4.7 (=2.17) `items[0]` in R2-02-02.

## 5. TIERING / BUDGET

5.1 **R2-00-01/02 at PR tier blow the 15-min PR ceiling** (harness §8). Each carries a 300 s init→first-comment budget *plus* `npm create vite` + `npm install` + `npm run build`, with one automatic whole-stack retry (`R2-00-tests.md:24` step 8) → ~20 min worst case for the pair alone. Edit: keep only `static` (no `npm install`) at PR tier; move `vite` to nightly with the other stacks.

5.2 **R2-05-08 is PR-tier on the `mail` layer**, but harness §8 defines the PR set as "api + widget + cli specs" with mail in the nightly phase. Edit: either retier R2-05-08 to nightly, or amend harness §8 to include a PR mail phase (it needs mailpit up on every PR run).

5.3 **R2-04's tier split is correct** (PR = R2-04-04/05 API rows; nightly = poll-dependent widget rows) and matches harness §9. **R2-03-03/04 correctly nightly**, sharing one restart window (2 restarts, under the ≤3 budget). **R2-02's five PR rows** each spawn a node stdio child plus git — acceptable.

5.4 **R2-06-01/04 at PR tier is fine** but note 04 step 3's `page.reload()` re-boots the widget (widget-status + branding + statuses + comments + capture-config); budget ~5 s, acceptable.

## 6. VERDICT

- **R2-00-tests.md — NOT-READY.** The two hand-off rows assert a message that contradicts the AC's own source (2.1); three rows target toolbar selectors that do not exist in a fresh context (2.3); the innerText sweep uses an impossible selector (2.4); PR tiering breaks the harness budget (5.1). Fix 2.1–2.4, 2.18, 3.6, 3.7, 3.11, 4.4, 4.5, 5.1 and it is ready.

- **R2-01-tests.md — NOT-READY.** R2-01-03 and R2-01-05 assert mutually exclusive fixture states (2.6, 2.7) — as written the spec cannot go green regardless of implementation quality. Add a third ready comment, fix `mkdir -p src` (2.5), and soften/strengthen the AC-1 claim (1.1).

- **R2-02-tests.md — READY-WITH-EDITS.** Define `id2` in R2-02-04 (2.8); set `commitStyle: 2` in the preconditions (2.16); find the canary by id rather than `items[0]` (2.17); add `pid`/`exited` to the `connectMcp` contract (3.8); add a row exercising the other five tools (1.2) and schema property/enum checks (1.3).

- **R2-03-tests.md — READY-WITH-EDITS.** `Pointer__SkillVersion` (2.9); correct the `installPointerSh` contract to install.sh's real interface (2.10); move R2-03-04 step 1 outside the restart window (2.11); assert the real `.agents/<skill>/SKILL.md` symlink instead of a hand-made `.agents/pointer.sh` (1.5).

- **R2-04-tests.md — NOT-READY.** Its headline AC (badge appears within 70 s without reload) cannot be proven by the step order given (1.6), and the AC's named surface — the launcher badge — is never asserted (1.7). Plus the environment filter blocks the card (3.5), `preAuthWidget` is not importable/1-arg (2.12), and the alpha-on-4173 fixture conflicts with R2-05 (2.13).

- **R2-05-tests.md — NOT-READY.** R2-05-06 step 4 asserts a 429 on `POST /api/auth/login`, which a binding unit test exists to prevent (2.15); R2-05-01/04's signed-in assertions target toolbar elements a fresh context never renders (2.3); the `#pf-user` email assertion is wrong (2.14); the 4173 fixture is contested (2.13). Fix those four plus 3.6 and 3.10 and it is ready.

- **R2-06-tests.md — READY-WITH-EDITS.** Switch the widget to `local` before the card assertions (3.5); mark `F` ReadyToApply so the apply-queue cell is non-vacuous (1.8); export `POINTER_API_KEY` for `pointer.sh` (3.9); resolve the 4173 fixture (2.13). Its header-gate matrix (both directions, five surfaces incl. legacy `pointer.sh`) and the raw-`text()` substring rule (`R2-06-tests.md:15,44`) are correct and match `execution/R2-06-secrets-flag.md:54-66,85` — the `ApplyReplyDto` reference is right, since R2-06 task 4 replaces `CommentApplyItemDto.Replies` (`Application/DTOs/Comment/CommentApplyItemDto.cs:26`).
