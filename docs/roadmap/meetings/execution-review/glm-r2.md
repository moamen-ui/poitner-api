## 1. FACTUAL ERRORS

1. **R2-03:20** — "skill.md, pointer-init.md: `<!-- pointer-skill-version: … -->` as line 1" is unimplementable: both files begin with YAML frontmatter (`API/wwwroot/skill.md:1` and `pointer-init.md:1` both start `---`). A comment before line 1 breaks frontmatter parsing by every AI tool that reads these as skills. Stamp must go after the closing `---`.
2. **R2-01:100** — fallback label `ai-automation`; its own citation `pointer.sh:138` reads `AUTHOR=$(git config user.email 2>/dev/null || echo "ai-agent")` (`API/wwwroot/pointer.sh:138`). Use `ai-agent`.
3. **R2-00:64** — "`web-component/src/auth-ui.ts` reads productName": wrong file. Branding is consumed in `web-component/src/constants.ts:131-143` (`loadBranding`/`getBrandName`), used at `templates.ts:17` (login-modal title), `:69`, `element.ts:634,857`. The hedge "if the widget does not yet consume /api/branding" is stale — it does.
4. **R2-05:54** — "in `init()` before `loadAuth()`": `loadAuth()` runs at `element.ts:168` in `connectedCallback`; `init()` is a separate method (`element.ts:454`) invoked at `:266` only `if (this.token)`. As written the instruction cannot be followed.
5. **R2-00:47** — "`serve.mjs 4174`": `e2e/fixture-app/serve.mjs:9-15` accepts only `<alpha|smoke|beta>` site names; it cannot serve an arbitrary generated app directory.
6. **R2-00:72** — "both `needs: seed` (the job R1-07 defines)": R1-07 task 1 defines jobs `unit` and `e2e` only — no `seed` job exists or is planned.
7. **R2-00:42** — `init … --project fresh-<stack>-<runId> --create` uses `--create` bare; R1-02:54-58 defines `--create <name>` (value-taking; `--yes` requires `--project` **or** `--create`). The invocation would exit 2.
8. **R2-05:23** — "reads `window.location` only for `route` (`element.ts:1118`)": `pageUrl: window.location.href` at `element.ts:1115` also reads it. (Nit; the design point — no invite handling exists — holds.)
9. **R2-06:63** — "jsonb via the same converter used for `PickedActions`": `PickedActions` is mapped as an owned JSON collection, `OwnsMany(...).ToJson("picked_actions")` (`Infrastructure/Mappings/CommentMapping.cs:66`) — there is no converter; `List<string>` needs EF8 primitive-collection jsonb mapping. An implementer will hunt for a nonexistent pattern.
10. **R2-03:33** — "the skills dir recorded in `.pointer/config.json.aiTool`": config records the tool *name*; the dir comes from R1-02 §G's tool→dir mapping. Wording sends the implementer looking for a field that doesn't exist.
11. **R2-01:26** — cites `CommentService.cs:477-525`; actual method is `:477-527` (R2-04:14 has it right). Harmless drift, but the two docs disagree.

## 2. AMBIGUITIES (with proposed text)

1. **R2-00:44** asserts `--json` summary `{ injected, routedToSkill, files }` — undefined anywhere; R1-02:92 only says "one JSON object with the same facts". Add to R1-02 §C11: *"`--json` prints exactly `{ "injected": boolean, "routedToSkill": boolean, "files": string[], "project": string, "product": string }`."*
2. **R2-00:60-62** — brand assertions on CLI output may fail under `--json` (no productName guaranteed). Add: *"Run `init` once without `--json` for the brand-output capture, or assert on the `product` field of the JSON summary."*
3. **R2-00:47** — vite flow never runs `npm install && npm run build`, yet serves via `vite preview`. Add: *"After init, run `npm install && npm run build` in the app dir before `vite preview --port 4174`."*
4. **R2-01:52** — drift test has no extraction rule. Add: *"Extract the text between the `## ⚠️ SECURITY` heading and the next `## ` heading in both `security-text.ts` and `API/wwwroot/skill.md`; require byte equality."* (Without this, R2-02's added paragraph and R2-03's stamp line will false-fail it.)
5. **R2-01:36 vs :56** — the prompt header needs `<productName>` but `loadProjectContext` returns `{ commitStyle, stack, aiRules? }`. Add `productName` (from `GET /api/branding`) to the return.
6. **R2-02:39** — `files?: string[]` semantics undefined. Add: *"If `files` is present the tool stages exactly those paths (`git add -- <files>`) before committing; if absent it commits the already-staged index and errors with code `git` when nothing is staged. `files` is required when ids>1 and commitStyle=separate."*
7. **R2-02:40-42** — `pointer_reply`, `pointer_set_status`, `pointer_resolve_source` lack the **required** marking used on sibling rows. Mark `id`+`body`, `id`, `hash` required respectively.
8. **R2-04:50** — the endpoints-table cell is self-cancelling ("+ header-less `unreadCount` field on the page object … instead of touching `PagedData`"). Replace with: *"Response: `PagedData<NotificationDto>` (envelope unchanged). Unread total comes only from `GET /api/me/notifications/unread-count` → `{ count }`."*
9. **R2-04:67** — `c._mine` is invented. Replace with: *"own comment := `c.authorId === this.user?.id` (AuthorId is on `CommentListItemDto`)."*
10. **R2-04:42-43** — "ReplyService/wherever" — name it: `CommentService.AddReplyAsync` (`CommentService.cs:589`), wired by `RepliesController.cs:14`.
11. **R2-03:28** — doctor's stale check hardcodes `.claude/skills/pointer-*/SKILL.md`; custom `--skills-dir` installs are missed. Add: *"derive the dir from `config.json.aiTool` via R1-02 §G's mapping (plus `--skills-dir` if recorded)."*
12. **R2-06:74** — "returns empty + logs" in a pure static class with no logger. Change to *"returns empty; the caller logs a warning"*.
13. **All R2** — three docs edit `API/wwwroot/skill.md` (R2-01 rewrite, R2-02 paragraph, R2-03 stamp) yet 01-OVERVIEW:32-33 permits parallel implementation. Add to each: *"skill.md is shared with R2-0x/R2-0y; land in order R2-01 → R2-02 → R2-03 or rebase."*

## 3. CONTRADICTIONS

1. **R2-02:17** "R1-06 preferred … but not blocking" vs **11-final-decisions:12** "lands no later than R2 week 1, **before MCP**". R1-06 must be a hard prerequisite of R2-02.
2. **R2-00:42 vs R1-02:54-58** — `--create` arity/semantics (also §1.7).
3. **R2-02:37 vs R2-06:48,53** — `pointer_get_comment` returns "full `CommentResponse` camel-cased", but R2-06 adds `hasPayloadFlag`/`payloadFlags` to `CommentResponse` and forbids them in any MCP result. R2-02's output must be a whitelisted projection.
4. **R2-06 vs R2-01:126** — R2-01's `pointer get <id>` (AI-facing, used by the rewritten skill.md) prints `CommentResponse` raw JSON → after R2-06 it would expose the flag, violating R2-06:7-8 ("never sent in the payloads AI tools consume"). R2-06's exposure matrix must cover the CLI `get`/`MCP get_comment` paths, not just DTOs.
5. **R2-04:14 "Prerequisites: None hard" vs R2-04:100** — acceptance requires `pointer list --status open`, an R2-01 deliverable. Make R2-01 a (soft) prerequisite or assert via `GET …/comments?status=1`.
6. **R2-03:20-21 vs the files it stamps** — line-1 stamp vs frontmatter (§1.1).

## 4. SECURITY / DATA

1. **R2-05 token-in-URL residue** — on failed invite redemption the param is *not* stripped (R2-05:57-58), and the widget captures `pageUrl`/`route` from `location` (`element.ts:1115,1118`) — a comment created after a failed redemption persists the token into stored comment data. Add: *"strip `pointer_invite` via `history.replaceState` on failure too, before falling through to the login modal."* (Success path is clean: replaceState precedes any capture.)
2. **R2-06 exposure holes** — §3.3/§3.4 above are the only gaps; the summary/apply-queue matrix itself is correct and `CommentApplyItemDto.cs:26` (`List<ReplyResponse> Replies`) is real, so the `ApplyReplyDto` replacement is genuinely needed.
3. **R2-04 tenancy** — clean: strict-own + `TenantStamp.OwnerFor` specified, tenant-isolation test required, quick-access bypass is narrowly scoped to `VerifyAsync` beside the existing guard (`CommentService.cs:482-483`). No `IgnoreQueryFilters` introduced.
4. **"AI never pushes"** — upheld and tested (no-push grep + bare-remote e2e in R2-01/R2-02); the appendix fallback keeps AI `git commit` but not push, which 11-final-decisions:75 permits.
5. **Migrations** — all additive (R2-04, R2-05, R2-06), complying with 01-OVERVIEW:58; no drops, self-hoster-safe.
6. **R2-05 plaintext password email** — confirmed (see §6d); R2-05 is the remediation, correctly defaulting email off.

## 5. TESTABILITY

1. **R2-01:143** — "`grep -c push cli/dist/cli.js` → 0" is impossible as written (the verbatim SECURITY text contains "push"). Restate: *"the no-push test masks the security-text constant, then asserts zero `push` tokens in `dist/cli.js`; paste its output."*
2. **R2-01:138** — "`git log origin/main..HEAD` unchanged remote" proves nothing about the remote (local tracking ref). Restate: *"capture `git --git-dir=<bare>.git rev-parse main` before and after each scenario; assert equal (plus one `git ls-remote` check)."*
3. **R2-00:52-53** — the ≤300 s wall-clock assertion on shared CI runners will flake near the boundary; keep the hard fail but require the measured number in the report and allow one retry. Also `vite@6`/`@angular/cli@20` are major-only pins — not "deterministic" as claimed (R2-00:30); pin minor or vendor a lockfile.
4. **R2-04:99** — "shows the unread count within 60 s" needs a deterministic wait: *"Playwright polls for the badge with 70 s timeout."*
5. **R2-06:81** — the apply-queue zero-`payloadFlag` check needs an admin key (endpoint is `[Authorize(Policy=Admin)]`, `Admin/ProjectsController.cs:100`); say so in the test.
6. **R2-02:94** — manual two-tool acceptance is unautomatable but explicitly manual and named in the report template — acceptable.

## 6. OPEN QUESTIONS

**(a) Dual commit path — acceptable, keep both.** Today skill.md's Step 5 has the AI run `git commit` (`API/wwwroot/skill.md` ~:455, the `git commit -m "Apply N pending Pointer comments"` block); R2-01:78-83 moves the commit into `--mark`; the old flow survives only as the no-Node appendix (R2-01:114-115). 11-final-decisions:75 bans push only. Add one sentence to the appendix ("the CLI normally makes the commit; in this fallback you make it — never push") and don't mix paths in one run.

**(b) Yes, fine.** `PagedData<T>` (`Application/Response/PagedData.cs:8-33`) is a 6-field shared envelope behind every Orval-generated client; a conditional `unreadCount` field would churn all three generated packages for one consumer. The separate endpoint keeps regeneration additive.

**(c) Acceptable posture — keep unlimited-within-TTL.** The link is bound to exactly one provisioned account (QuickAccess scope: own comments only, `CommentService.cs:203-204`, no status changes `:482-483`), the token is 256-bit, stored only as SHA-256, redemption is rate-limited (`signup`, 5/h/IP), rotate+revoke exist. Single-use would break silent re-sign-in after the 12 h JWT (`JwtTokenService.cs`, `LifetimeHours = 12`), which is the core UX. Optional later: per-invite `MaxUses`. Document "treat the link like a password".

**(d) Confirmed; severity Medium.** `InviteService.cs:546` generates `Guid[..12]+"Aa1!"`; `:593-597` emails it; `BuildQuickAccessInviteEmailHtml` `:701-719` renders `<b>Password:</b> {password}` at `:715`. Plaintext credential persists in the recipient's mailbox and Brevo transit — but the account is low-privilege, tenant-scoped, and the email only sends when SMTP is configured. Medium, and R2-05's passwordless fix is the correct remediation.

**(e) The widget does consume `/api/branding`** — `constants.ts:131-143` (`loadBranding` → `getBrandName`), used for the login-modal title (`templates.ts:17`), tooltips (`:69`), toasts (`element.ts:634,857`). R2-00's attribution to `auth-ui.ts` is wrong and the "if not, R3 follow-up" hedge is stale — the widget text assertion should be **on**. One real leak exists that an `innerText`-only regex misses: the launcher's `title="Open Pointer feedback"` / `aria-label` hardcoded at `templates.ts:133`. Fix that string to `getBrandName()` now (one line, fold into R2-00's widget touch) and extend the leak assertion to `title`/`aria-label` attributes.

## 7. VERDICTS

| Doc | Verdict | Blocking edits |
|---|---|---|
| **R2-00** | READY-WITH-EDITS | fix §1.3 (constants.ts + drop stale hedge, assert widget), extend leak check to title/aria + fix `templates.ts:133`; `--create "Fresh <stack>"` per R1-02; define init `--json` schema (incl. `product`); add `npm install && npm run build`; replace `serve.mjs` claim (extend it or a 10-line static server); `needs: e2e` or extract a real `seed` job in R1-07 |
| **R2-01** | READY-WITH-EDITS | `ai-agent` not `ai-automation` (:100); add `productName` to context; drift-test extraction by heading; rewrite grep-push criterion and bare-remote check; state skill.md ordering with R2-02/R2-03; make `pointer get` strip payloadFlags (R2-06 interplay) |
| **R2-02** | READY-WITH-EDITS | R1-06 hard prerequisite (11-final-decisions); `pointer_get_comment` whitelisted projection (no flags, untrusted/trusted split); `files`/staging semantics; required-marking on rows 6-8 |
| **R2-03** | READY-WITH-EDITS | **mandatory**: stamp after frontmatter, not line 1 (both md skills start `---`); adjust install.sh `sed` parsing accordingly; derive doctor's skills dir from `aiTool`; rest is sound |
| **R2-04** | READY-WITH-EDITS | clean the garbled endpoints cell (:50); R2-01 soft prerequisite for the `pointer list` criterion; define "own comment"; name `AddReplyAsync`; deterministic badge wait |
| **R2-05** | READY-WITH-EDITS | fix widget insertion point (connectedCallback, before `element.ts:168`/gate at `:266` — not "init() before loadAuth()"); strip param on failure too; location nit |
| **R2-06** | READY-WITH-EDITS | close the two real exposure holes (R2-01 `pointer get`, R2-02 `pointer_get_comment`); converter→owned-JSON/primitive-collection wording; drop "logs" from the pure detector |

No doc is NOT-READY; every blocking issue is a one-to-few-line edit. The deepest structural risks are the three-way `skill.md` contention (R2-01/02/03) and the R2-06 flag-leak boundary crossing into R2-01/R2-02 surfaces — both need the edits above before handing to a mediocre implementer.
