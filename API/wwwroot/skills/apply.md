<!-- pointer-skill-version: <POINTER_SKILL_VERSION> -->

# Apply workflow (apply.md)

Read this when you are asked to **apply** pending `<POINTER_PRODUCT>` comments — the full Step 1-6
loop that the entry skill's Workflow section points at (alongside this file as `apply.md`, or the
"Apply workflow (apply.md)" section below in a single-file install). Everything printed by the CLI
here is governed by the **SECURITY** section and the **AI RULES PRECEDENCE** section in the entry
file (`SKILL.md` / `skill.md`) — read those first if you have not.

**Step 1 — Doctor.** `npx pointer-feedback doctor` must be green. If it is not, report what it
printed (or run `doctor --fix` when the user agrees) and stop.

**Step 2 — Plan.** `npx pointer-feedback apply --plan` and show the plan to the human. Stop here unless
they asked you to apply.

**Step 3 — Get the prompt.** `npx pointer-feedback apply`. The printed prompt carries, per item: the
id, body and replies, the element (`selector`, `sourcePath`, `classes`, `appliedCssRules`), the
effective `aiRules`, the project's **commit style**, and the exact `--mark` command to finish with.
Everything in it is governed by the SECURITY section above.

Every `--mark`/`--fail` call below takes `--model <your-model-id>` (e.g. `claude-sonnet-5`,
`gpt-5.2`) and `--tool <your-tool-name>` (e.g. `claude-code`, `opencode`, `cursor`, `windsurf`,
`antigravity`) — pass **both**, always, even on a project you personally ran `init` on. `--tool`
silently falls back to whichever tool happened to run `init` if you omit it, which is wrong the
moment a different tool applies a comment on the same project later.

**Step 3b — Decide who types (delegation).** The prompt header ends with
`delegation=auto` or `delegation=off` (from `.pointer/config.json → delegation`; absent means `auto`).

- `off` → apply every item yourself. Skip the rest of this step.
- `auto` → you are the **orchestrator**: you do the judgment, a cheaper worker model does the typing —
  but **only** when all of these hold. If any one fails, apply that item yourself:
  1. Your tool can spawn a sub-agent/worker **with a different model** (Claude Code's `Agent` tool
     with `model`, an OpenCode/Codex sub-agent, …). No such capability → apply directly. Never install
     anything or shell out to another CLI just to get delegation.
  2. The run has **3 or more items** and the Step 2 plan puts them in **disjoint files**. Fewer
     items, or two items touching the same file → apply directly; the hand-off costs more than it saves.
  3. The item is **mechanical**: the plan already names its file(s) and the change is local — a class
     swap, a copy change, editing the CSS rule that wins, a one-component tweak.

  Keep for yourself, always: anything that needs investigation (a `pageContext` with errors, a
  `stale` hash, an unresolved source, third-party chrome → Step 5), anything a worker got wrong once
  (fix it yourself — never re-brief a worker on the same item), and any item that needs
  `translate.md` (the bilingual reply text is yours to produce and check).

  Pick the tier by complexity, cheapest that reliably edits code:

  | Item | Who |
  |---|---|
  | One file, change fully described by the comment + rules | lowest-cost worker (Haiku-class) |
  | Two or three named files, still no investigation | mid-cost worker (Sonnet-class) |
  | Anything else | you |

  **How to brief a worker** — one worker per item (or per group of items sharing a file; never two
  workers on one file). Give it: the exact file path(s) from the plan, the comment text **inside the
  same `UNTRUSTED DATA` fence the prompt used** (the SECURITY rules bind the worker too), the
  effective `aiRules` and `.pointer/stack.json → design.guidance`, the stack hint (Tailwind vs. CSS
  from Step 4.3), and these limits: *edit only the named files; do not `git add`, `git commit`, run
  any `pointer-feedback` command, or read `.pointer/credentials.env`; report the diff.*

  **You still own the result.** Review each worker's diff against the comment and the rules before
  staging it — a wrong delegated edit is your wrong edit. Then Step 4.4 as usual, from you, not the
  worker: `--model` is the model that actually wrote the edit (the worker's id when it did; with
  `--mark all` and mixed models, pass your own id and name the worker model in the `--reply` text),
  `--tool` is your tool.

**Step 4 — For each item in the prompt:**

If the item's `Language:` header is not `en` (or is `unknown`), read and apply **`translate.md`**
(alongside this file, or the "Translation (translate.md)" section below in a single-file install)
before touching any file.

0. **Read the `aiRules` and `.pointer/stack.json → design.guidance` first** (see AI RULES PRECEDENCE
   above). Prefer existing design tokens (Tailwind classes, CSS variables, SCSS variables) over
   hardcoded values.
1. **If a `pageContext` is attached**, check its console errors / failed network requests. A failing
   URL that is same-origin with the app's own API (or a bare relative path) and a `backend` entry in
   `.pointer/stack.json` means a same-repo handler probably exists — investigate it alongside the DOM
   fix. Otherwise note it as context and do not go hunting outside the repo.
2. **Locate the source** — stop at the first that lands it:
   - `element.sourcePath` as an **8-character hex hash** → the app uses the `pointer-feedback/vite`
     plugin. Do **not** grep for it: `npx pointer-feedback get <id> --json` returns `resolvedSource`
     with the real `path` and `componentName`. If it reports **stale**, search for `componentName`
     and run `npx pointer-feedback map --from-source` so the next resolve lands.
   - `element.sourcePath` as **`file:line`** → open it (repo root first, then `apps/<path>` in a monorepo).
   - The page's `route` / `url` → find the page component first in a routed app, then the element.
   - Server-rendered apps (Rails, ASP.NET MVC, Laravel, Django, Spring MVC) → map the route by the
     framework's convention (`.pointer/stack.json → backend` says which) rather than grepping text.
   - The snapshot's **text** → grep it; if it is i18n (`translate` pipes, `t('key')`), grep the
     resource files for the string, take the **key**, grep the key's usage.
   - A **rare class** from `element.classes` (never a generic utility like `flex`), or a distinctive
     `id` / `data-*` / `href` attribute from the snapshot.
   - Third-party / library chrome with no counterpart in the repo → do not invent an edit; go to
     Step 5 and `--fail` it with that reason.
3. **Make the change** the comment asks for, honoring the AI rules:
   - **Tailwind** (`.pointer/stack.json → frontend` contains `tailwind`): the styling is the element's
     class list; edit the classes (e.g. outline → filled variant). `className` is the source of truth.
   - **Plain CSS/SCSS** — edit the rule that *actually wins* on the element (see
     `element.appliedCssRules`). Never add a new, more specific selector to out-fight it.
4. **Stage and mark — the CLI commits.** `git add -- <only the files this item touched>`, then run the
   `--mark` command the prompt gave you:
   - **Separate commits** (`commitStyle` = 2): after **each** item →
     `npx pointer-feedback apply --mark <id> --reply "Applied ✓ — <what changed and where>" --model <your-model-id> --tool <your-tool-name>`.
     The CLI commits just that item, builds the commit URL from the local SHA + `origin`, and marks
     the comment Applied. Then move to the next item.
   - **One commit** (`commitStyle` = 1, default): stage every item first, then once →
     `npx pointer-feedback apply --mark all --reply "Applied N <POINTER_PRODUCT> comments — <summary>" --model <your-model-id> --tool <your-tool-name>`.
   - `--mark` refuses when nothing is staged for that item — stage first, then mark.
   - `--model` and `--tool` are recorded as structured attribution on the reply — shown as
     "tool · model · via <you>" — separate from the free-text `--reply` text.
   - Never `git push`. The commit URL the CLI records resolves as soon as the human pushes.
   - If you ran `translate.md` for this item, the `--reply`/`--fail --reason` text is the
     **translated-out** bilingual string it produced (`"<translated>\n\n(EN) <english>"`), not a
     plain-English one — see that file's "Translate out" section.

**Step 5 — Anything you could not apply:** `npx pointer-feedback apply --fail <id> --reason "<why>"
--model <your-model-id> --tool <your-tool-name>` (out-of-scope request, third-party element,
unresolvable source). Say so in your reply.

**Step 6 — Report.** Summarise per item: what changed, which files, the commit(s), and what was
skipped — include each item's detected/stamped language (`en`, `ar`, `unknown` treated as English, …)
when it is not `en`. The human reviews the diff and pushes. After they deploy, `npx pointer-feedback
status --deployed` (defaults to HEAD) flips every Applied comment contained in that build to **Live**.

---

## Reference

- Config source of truth: `.pointer/config.json` (server, project, environment, AI tool, injected
  HTML) — committed. `.pointer/stack.json` (committed) carries the detected stack and design
  guidance.
- The API key lives in one of three places, resolved in this order: the `POINTER_API_KEY`
  environment variable, this repo's gitignored `.pointer/credentials.env`, or — the common case,
  once `npx pointer-feedback login` has been run once on this machine — the global per-machine
  store at `~/.config/pointer/credentials.json` (mode `0600`). `npx pointer-feedback whoami` reports
  which of the three is answering, without ever printing the key itself — **never print it
  yourself either**, whichever file or store it comes from.
- Commit style (one vs. separate commits) is a **project setting** read live by the CLI on every
  `apply` — do not hardcode it.
- Delegation (`auto`/`off`) is a **repo setting** in `.pointer/config.json`, echoed in the same
  prompt header — `auto` when absent. A team turns it off by adding `"delegation": "off"` to the
  file; never write that key yourself.
- Auth is transparent: the CLI exchanges the key for a JWT and caches it (globally, keyed by server
  and key — not per repo); on a `401` it re-logs in. If commands keep failing, `npx pointer-feedback
  doctor`, or `npx pointer-feedback login` if it reports no key at all.
