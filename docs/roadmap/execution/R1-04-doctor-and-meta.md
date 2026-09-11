# R1-04 — `pointer doctor` + `GET /api/meta` (§3, §4 · Release 1 · ≤ 1 day)

## Goal
`npx -y pointer-feedback doctor` tells a developer (or an AI agent, as its first step) exactly what is
wrong with an install in one screen, and `GET /api/meta` lets the CLI detect a server that is too old
or a CLI that is too old.

## Out of scope
- `pointer update` and skill-version stamping (R2-03) — `doctor` gets a "skills up to date" line then; here it only checks presence.
- Health/readiness probes for ops (use `/swagger` today; a real `/health` can come with NEW-3).

## Prerequisites
- R1-02 (`cli/src/checks.ts` exists; `doctor` is a thin command around it).
- Facts: no `/api/meta`, `/api/version` or `/api/health` exists (`00-API-INVENTORY.md` §4). Rate-limit policies live in `API/Extensions/RateLimitingExtensions.cs`. `PlansPublicController` is the pattern for an anonymous, rate-limited GET.

## Design

### `GET /api/meta` (anonymous)
- `API/Controllers/MetaController.cs` — `[Route("api/meta")]`, `[AllowAnonymous]`, `[Produces("application/json")]`, `[EnableRateLimiting("meta")]` — a **dedicated** policy (120/min/IP, defined in `RateLimitingExtensions.cs`; do not share `plans`: a NAT'd office running CLIs must not eat the landing page's quota or vice versa), `[ProducesResponseType(typeof(MetaResponse), 200)]`, `ResponseCache` 60 s public.
- `Application/DTOs/Meta/MetaResponse.cs`:
  ```csharp
  public sealed class MetaResponse {
    public string Version { get; set; }        // assembly InformationalVersion, e.g. "1.4.0+abc1234"
    public int ApiVersion { get; set; }        // hand-bumped integer, starts at 1; bump on any breaking change
    public string MinCliVersion { get; set; }  // semver, from configuration "Cli:MinVersion", default "0.0.0"
    public string? SkillVersion { get; set; }  // null until R2-03
    public string ProductName { get; set; }    // from BrandingService (white-label)
    public DateTime ServerTime { get; set; }   // UTC — lets the CLI detect clock skew for JWT issues
  }
  ```
- Service: `IMetaService` in `Application/Services/Interfaces`, `MetaService` in `Application/Services/Implementation` (per 01-OVERVIEW conventions). `GetAsync(string publicBase)` reads `IConfiguration["Cli:MinVersion"]`, the product name via `IBrandingService.GetAsync(publicBase, existingKinds)` — that signature needs a public base URL, so the controller resolves it exactly as `BrandingController.cs:34-42` does (`PointerUrlResolver.ResolvePublicUrl(configuration, Request)`) and passes it in — and `Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()`.
- `ResponseCache` 60 s means `ServerTime` can be up to 60 s stale for a cached client — well under the 5-minute skew threshold the CLI uses; acceptable, and the CLI must not tighten that threshold below 2 minutes.
- Policy ownership: the `meta` rate-limit policy is **added by this doc** in `RateLimitingExtensions.cs` (R1-02 adds `events`, R1-05 adds `comments`/`login`; expect a trivial merge conflict, never duplicate a policy name — a duplicate `AddPolicy` throws at startup).
- `API/appsettings.json`: add `"Cli": { "MinVersion": "0.1.0" }`; `docker-compose.prod.yml` may override with `Cli__MinVersion`.
- Add `<InformationalVersion>` / `<Version>` to **`API/Pointer.API.csproj`** (the only csproj under `API/`; the Dockerfile publishes it) if absent; set from `git describe` in the Dockerfile build arg (`ARG APP_VERSION`; `-p:InformationalVersion=$APP_VERSION`). Default when unset: `0.0.0-dev`.

### `pointer doctor` (CLI)
```
pointer doctor [--server <url>] [--project <key>] [--json] [--fix]
```
Runs the checks in `cli/src/checks.ts` in this order and prints one line each (`✔`, `✘`, `⚠`), then a
summary. Every check returns `{ id, status, message, hint? }`. **Exit-code precedence (exact):**
1. `meta` ✘ because `semver(cli) < minCliVersion` → print that line, **stop immediately** (skip the remaining
   checks — they may be meaningless against a newer server), exit **5**.
2. Otherwise run every check; if `key` is ✘ → exit **3**.
3. Otherwise any ✘ → exit **1**; all ✔/⚠ → exit **0**.
(`--json` always emits the full `checks` array for the checks that ran.)

| id | ✔ when | ✘/⚠ message + hint |
|---|---|---|
| `config` | `.pointer/config.json` parses and has `server`+`project` | ✘ "No .pointer/config.json — run `npx -y pointer-feedback init`" |
| `server` | `GET {server}/api/branding` 200 within 3 s | ✘ "Cannot reach {server}" |
| `meta` | `GET /api/meta` 200 and `semver(cli) ≥ minCliVersion` | ✘ "CLI {v} is older than the server requires ({min}) — run `npx -y pointer-feedback@latest …`" (exit 5); ⚠ if `/api/meta` is 404 ("server predates /api/meta") |
| `clock` | `abs(now − serverTime) < 5 min` | ⚠ "Clock skew {n}s — logins may fail" |
| `key` | `credentials.env` has `POINTER_API_KEY` and `login-with-key` returns `status:"ok"` | ✘ "API key missing/invalid — Profile → API key" (exit 3) |
| `project` | project `key` in `GET /api/admin/projects` and active for `config.environment` (`IsActiveLocal/Staging/Production`) | ✘ "Project {key} not found in this workspace" / ⚠ "Project inactive for {env}" |
| `widget` | detection finds the marker block (`<!-- pointer-feedback:start -->`) or a `<pointer-feedback` tag, or `.env` has `VITE_POINTER_PROJECT` | ⚠ "Widget not found in this app — run `init` or the pointer-init skill" (not ✘: Next/Angular installs are legit) |
| `widget-served` | `GET {server}/pointer.js` 200 with `content-type` containing `javascript` | ✘ "Widget script not served" |
| `skills` | expected skill files for `config.aiTool` exist (table in R1-02 §G) | ⚠ "Skills missing for {tool}" (`--fix` reinstalls) |
| `gitignore` | frozen lines present and `credentials.env` not tracked (`git ls-files --error-unmatch` fails) | ✘ "credentials.env is tracked by git!" / ⚠ missing ignore lines (`--fix` adds) |
| `stack` | `.pointer/stack.json` exists (R1-02 `init` writes it from the `POST /stack` response) | ⚠ "Stack not registered" (`--fix` re-detects, POSTs and writes the file) |

`--fix` applies only the idempotent repairs marked above. `--json` prints `{ ok: boolean, checks: [...] }`.
After the run, `POST /api/events {type:"doctor_run", meta:{ok, failed:[ids]}}` when `key` passed.

### skill.md hook
Add to `API/wwwroot/skill.md` Step 1: "If `npx` is available, run `npx -y pointer-feedback doctor --json`
first and stop with its hints if `ok` is false." (Keeps working for installs without the CLI.)

## Tasks
1. API: `MetaResponse`, `IMetaService`/`MetaService`, `MetaController`, config key, csproj version, Dockerfile `ARG APP_VERSION` + `docker-compose.prod.yml` build arg (`APP_VERSION: ${APP_VERSION:-0.0.0-dev}`).
2. API tests: `Tests/MetaEndpointTests.cs` — 200 anonymous, fields present, `MinCliVersion` from config, product name from branding.
3. CLI: `src/commands/doctor.ts`; semver compare helper (no dependency: split on `.`/`-`); `--fix`; `--json`; exit codes.
4. CLI tests: `doctor.test.ts` against the stub server (each check's ✔/✘/⚠ path; `minCliVersion` too high → exit 5; 404 meta → ⚠).
5. `skill.md` Step 1 hook; `cli/README.md` doctor section.
6. `AGENTS.md`: mention `/api/meta` and the `Cli:MinVersion` knob.

## Dashboard tasks
- Regenerate services (`MetaResponse`, `GET /api/meta`). Optional: show server version in Settings → About (follow-up).

## Docs
**Creates `landing/docs/cli-reference.html`** with a `doctor` section: every check id, what a ✓/⚠/✘ means for it, what `--fix` repairs, the `--json` shape, and the exit codes (notably 5 = CLI older than the server requires, and what to do about it). Answers: *“my install isn’t working — what’s wrong?”* **Also updates `landing/docs/self-hosting.html`** (create if absent) with a two-line `GET /api/meta` note: what it reports and the `Cli:MinVersion` knob self-hosters can set.

## Tests
- API: `Tests/MetaEndpointTests.cs`.
- CLI: `cli/test/doctor.test.ts`, `checks.test.ts` (from R1-02).
- E2E scenarios `doctor-green-after-init`, `doctor-detects-tracked-credentials` — defined in `docs/roadmap/testing/R1-04-tests.md` (R2-00 remains the driver/CI-matrix host only).

## Acceptance criteria
- [ ] `curl -s $SERVER/api/meta | jq .data` shows `version`, `apiVersion`, `minCliVersion`, `productName`, `serverTime`.
- [ ] Fresh install after `init`: `doctor` prints all ✔ (or ⚠ only for `widget` on Next) and exits 0.
- [ ] `git add .pointer/credentials.env` (test repo) → `doctor` ✘ `gitignore`, exit 1.
- [ ] Set `Cli__MinVersion=99.0.0` on the API → `doctor` exits 5 with the upgrade hint; `init` refuses to continue with the same message.
- [ ] `doctor --json` is valid JSON and nothing else on stdout.

## Rollout / compatibility
Additive endpoint. CLIs against servers without `/api/meta` degrade to ⚠. `Cli:MinVersion` defaults to `0.0.0` so nothing is blocked until deliberately raised.

## Report template
Files changed · `just test`/`npm test` lines · pasted `doctor` output for the green and the tracked-credentials cases · `curl /api/meta` output.
