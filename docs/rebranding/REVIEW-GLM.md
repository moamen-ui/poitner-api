# Review — GLM-5.3 (opencode)

**Status: the direct GLM review of *this* plan did not complete.** What follows is (a) an honest
record of the attempts, and (b) the GLM findings that *were* obtained and verified — from GLM's
review of the sibling `docs/REBRANDING_MASTER_PLAN.md`, which was written against this same
codebase and therefore transfers.

## Attempts on this plan

| # | Invocation | Result |
|---|---|---|
| 1 | `opencode run -m zai-coding-plan/glm-5.3 --dir ~/Desktop/REPOS` on the live checkouts | Read all three subject files (`REBRANDING-PLAN.md`, `verify-no-pointer.sh`, `answers.template.yml`), printed "Now I'll verify the plan's measured claims against the actual codebase, starting with occurrence counts", then **died with exit 1** mid-run |
| 2 | Same, in an isolated worktree (`~/tmp/rebrand-review`) | **~25 min, zero bytes of output**, no report. Killed |
| 3 | Tighter prompt (max 12 findings, prioritised, "write the file even if partial"), `--dir` narrowed to the API worktree | **~20 min, zero bytes of output**, no report. Killed |

**Cause (high confidence):** a second `opencode` process was running concurrently for the whole
period — the parallel review session that produced `docs/reviews/GLM_REBRANDING_REVIEW.md`. This is
the documented `opencode` failure mode: concurrent runs contend on its SQLite state
("database is locked") and hang or die silently. Attempt 1's exit-1 mid-read fits it exactly.

**To finish this round later** — when no other `opencode` process is running:

```bash
opencode run -m zai-coding-plan/glm-5.3 --dir ~/tmp/rebrand-review/pointer-api "$(cat ~/tmp/prompt-glm3.txt)"
```

The prompt is preserved at `~/tmp/prompt-glm3.txt`; the isolated worktrees are still at
`~/tmp/rebrand-review/` (remove with `git worktree remove`). Nothing in the plan depends on this
round completing.

---

## GLM findings that were obtained and verified

From `docs/reviews/GLM_REBRANDING_REVIEW.md` (GLM-5.3 reviewing the sibling plan against this
codebase). Each was re-verified against the code before being accepted — see `REVIEW-RESPONSE.md`
round 3 for the per-finding verdicts. Summary of what it contributed to *this* plan:

| Finding | Verified | Where it landed |
|---|---|---|
| `/embed.js` is generated inline in `Program.cs:283-319` and hardcodes the tag, the `pointer.js` path and `window.__pointerEmbedded` | Yes | §5.3 (corrected my wrong claim that `embed.js` was a static asset), §8.2 |
| Extension postMessage protocol: `source: 'pointer-ext'` / `'pointer-ext-res'`, `PROXY_TOKEN = '__pointer_via_proxy__'`, plus `DEFAULT_SERVER` | Yes — `inject-main.ts:20,29,52,64`, `content-bridge.ts:22`, `background.ts:180,269` | §8.8, escalated with the Web-Store-rollout-is-not-atomic consequence |
| `POINTER_SWAGGER_URL`, with the old host hardcoded in the workflow | Yes — `publish-clients.yml:54`, `generate-clients.mjs:24` | §8.6 |
| `justfile` targets (`psql -U pointer -d pointer`, the `-R moamen-ui/poitner-api` flag, the `dev` echo) | Yes | §5.5, §8.2 |
| `docker-compose.prod.yml` + `.env.example` + `.env.prod.example` env surface (`Pointer__*`, `JWT__Issuer`, `Database=pointer`, `admin@pointer.local`) | Yes | §4.6, §4.7, §5.11, §8.2 |
| `clients/react/package.json:28` carries the `poitner` typo, so a replace keyed on `pointer-api` skips it | Yes | §7.3 set 1 — the reason the typo pass runs first |
| The audit must be case-insensitive and must exclude `.playwright-cli/` / `.playwright-mcp/` DOM snapshots | Yes — 316 + 68 + 189 brand-matching files | Gate `EXCLUDES` + §7.1; the gate now greps `-i` |
| Guardrail list must include the `@media (pointer: fine|coarse)` forms and the `.pointer-events-*` utilities | Yes | §7.2 protected list |
| Dashboard builds fail if the client packages aren't published first | By inspection | Already ordered correctly (§6.2); added the `file:`/`npm link` escape hatch and the warning not to commit it |

**What GLM's sibling review did *not* contain** (and so came from elsewhere): the `pf-` / `--pf-*`
surface, the JWT-issuer mass-401 mechanism, the runtime Branding system as a headline concept, the
`injectedFiles`/`<POINTER_SERVER>` coupling, the snake_case schema correction, and the
`comments.element` JSON-column hazard.
