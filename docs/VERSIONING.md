# Versioning & deprecation policy

This page is the single source of truth for how each part of Pointer is versioned, and what
consumers can expect when something changes. It covers: API (HTTP), the CLI (`pointer-feedback`
npm package), client packages (`@moamen-ui/pointer-react`), served files, and skill files.

## 1. API (HTTP)

All endpoints live at `/api/*`. `/api/v1/*` is a supported alias — a path rewrite in the API's
middleware pipeline maps every `/api/v1/{rest}` request to `/api/{rest}` before routing runs, so
both prefixes reach the same controller action with an identical response body. `/api/v1` is the
**canonical documented prefix** for external integrations going forward; `/api/*` continues to work
unchanged for existing clients (the CLI and `@moamen-ui/pointer-react` keep calling the unversioned
paths — see §3).

No `/api/v2` exists or is currently planned (see `docs/roadmap/execution/R5-68-versioning-policy-and-v1-alias.md`
§10 for what is explicitly out of scope: `Asp.Versioning.*`, header/query-string versioning,
versioned Swagger specs).

- **Additive changes** — new fields, new endpoints, new optional request parameters — are not
  breaking and ship without notice.
- **Breaking changes** — field removal, a field's type changing, or a behavioural change to an
  existing endpoint — are avoided wherever possible. When one is unavoidable:
  1. It is documented in the release notes.
  2. The affected endpoint is deprecated with a sunset header for at least one release cycle
     before the old behaviour is removed.
- Swagger (`/swagger/v1/swagger.json`) always documents the `/api/*` paths only; it does not show
  `/api/v1/*` as a separate, duplicated set of paths — the rewrite is transparent to Swagger because
  Swagger reads controller route metadata, not incoming request paths.

## 2. CLI (`pointer-feedback` npm package)

The CLI follows npm semver (`MAJOR.MINOR.PATCH`).

- A `Cli__MinVersion` config value on the server (`docker-compose.prod.yml`) can enforce a minimum
  CLI version; the CLI checks this on startup and prints a warning when it is below the configured
  minimum.
- **Deprecation window**: a CLI major version remains supported for 90 days after the next major
  version is published. During that window the old major keeps working against the API — the API
  stays backward-compatible with it — but it may miss newly added features.
- After the 90-day window, the old major is no longer the target of new API features but is not
  actively broken; there is no forced-upgrade mechanism beyond the `Cli__MinVersion` warning.

## 3. Client packages (`@moamen-ui/pointer-react`)

The React client is generated from the live Swagger spec via orval (see `orval.config.ts` /
`AGENTS.md` / `.claude/CLAUDE.md` for the generation pipeline). Its version follows the API's own
semver tag, and it calls the unversioned `/api/*` paths (not `/api/v1/*`) — this is unaffected by
this policy and is not changed by R5-68. Breaking changes to the client are gated by the API's own
backward-compatibility promise in §1: as long as the API does not break a field the client depends
on, the client does not need a breaking release either.

## 4. Served files

`/widget.js`, `/widget.css`, `/embed.js`, `/skill.md`, `/skills/apply.md`, `/skills/translate.md`,
`/skills/advanced.md`, `/pointer-init.md`, `/install.sh`, `/pointer.sh`, `/vendor/snapdom.js` are
**unversioned** — every request gets the latest build. The widget supports a `?v=<hash>` query
parameter to pin a specific build for reproducible embeds (R3-03); absent that parameter, the
latest widget is always served. `/install.sh` and the skill files are always the latest revision;
there is no versioned URL for any served file.

## 5. Skill files

`SKILL.md` and its sub-skills carry a `<POINTER_SKILL_VERSION>` stamp injected at serve time
(`Program.cs`, skill-injection middleware). The CLI's `doctor` command compares the version stamped
into the locally installed skill files against the version currently served by the API, and flags a
mismatch so a stale local skill can be refreshed via `init`/`update`.

## Summary table

| Surface | Versioning scheme | Canonical reference |
|---|---|---|
| API (HTTP) | Path-based; `/api/v1` alias to unversioned `/api/*`; additive-only in practice | this doc §1 |
| CLI (`pointer-feedback`) | npm semver; 90-day deprecation window per major | this doc §2 |
| `@moamen-ui/pointer-react` | Follows API semver tag; generated, never hand-versioned | this doc §3 |
| Served files (`/widget.js`, `/skill.md`, …) | Unversioned, always latest; `?v=<hash>` pin on the widget | this doc §4 |
| Skill files (`SKILL.md`, …) | `<POINTER_SKILL_VERSION>` stamp, compared by `doctor` | this doc §5 |
