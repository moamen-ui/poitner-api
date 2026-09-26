#!/usr/bin/env bash
# Local e2e gate — founder rule (2026-09-23): nothing deploys to production until the FULL e2e
# suite passes on an isolated local stack; CI only confirms.
#
# Reproduces exactly what `.github/workflows/e2e.yml`'s `e2e` job runs (`bash run-e2e.sh --nightly
# --ci`) against an ISOLATED compose project on alternate ports, so it never touches the shared dev
# stack (compose project `pointer-api`, API :8090/db :5433/mailpit :8025,1025/verdaccio :4873 —
# `e2e/scripts/reset.sh` wipes that stack, so this script must never run against it). See the
# `project-e2e-isolated-stack` memory note for the proven recipe this formalizes.
#
# Usage:
#   scripts/local-e2e-gate.sh [<source-dir>]
#
#   <source-dir>  Defaults to this checkout (the repo root containing this script). For a branch
#                 under test, pass a worktree path instead, e.g.:
#                   scripts/local-e2e-gate.sh /Users/you/repos/pointer-db16
#
# Env overrides:
#   E2E_GATE_WORKDIR   Scratch destination for the rsynced copy (default: a fixed path under the
#                      system temp dir). Wiped (rsync --delete) and reused between runs.
#   E2E_GATE_KEEP=1    Skip the final `docker compose down -v` (leave the gate stack running for
#                      debugging). Default: always tear down.
#   E2E_GATE_SKIP_INSTALL=1  Skip `npm ci`/`playwright install`/cli build (reuse whatever is
#                      already installed in the scratch copy from a previous run).
#   E2E_GATE_WITH_AI=1 Opt-in, PAID: appends `--with-ai` to the `run-e2e.sh --nightly --ci`
#                      invocation below, so Layer B (TC1-TC6, e2e/ai/run-cases.mjs) runs against
#                      real `claude`/`opencode` invocations, not just the zero-AI phases. Default
#                      behaviour (this var unset) is unchanged — no ai phase, no extra cost. The
#                      orchestrator decides when to set this; never set it to run the paid suite
#                      unprompted.
#   E2E_AI_TOOLS       Pass-through to e2e/ai/run-cases.mjs's own `TOOLS` list (comma-separated,
#                      default `claude-code,opencode-glm,antigravity`) — only read when
#                      E2E_GATE_WITH_AI=1. e.g. `E2E_AI_TOOLS=claude-code` to run only one tool.
#   E2E_AI_CASES       Pass-through to e2e/ai/run-cases.mjs's own case filter (comma-separated case
#                      ids from e2e/ai/cases/manifest.json, e.g. `tc3,tc6`) — only read when
#                      E2E_GATE_WITH_AI=1. Unset (default) runs every case. Combine with
#                      E2E_AI_TOOLS for a cheap targeted repro instead of the full TC1-TC6 sweep
#                      across every tool, e.g.:
#                        E2E_GATE_WITH_AI=1 E2E_AI_TOOLS=claude-code,opencode-glm \
#                          E2E_AI_CASES=tc3,tc6 bash scripts/local-e2e-gate.sh <worktree>
#                      The full AI suite is the same invocation with both vars unset:
#                        E2E_GATE_WITH_AI=1 bash scripts/local-e2e-gate.sh <worktree>
#
# Exit status: non-zero if any phase fails (matching run-e2e.sh's own semantics); the phase-by-phase
# table from e2e/state/report.md is always printed before exiting, pass or fail.
set -uo pipefail

GATE_SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${GATE_SELF}/.." && pwd)"

SRC_DIR="${1:-${REPO_ROOT}}"
SRC_DIR="$(cd "${SRC_DIR}" && pwd)"

SCRATCH="${E2E_GATE_WORKDIR:-/tmp/pointer-e2e-gate/src}"
mkdir -p "$(dirname "${SCRATCH}")"

if [ "$(cd "${SCRATCH}" 2>/dev/null && pwd || true)" = "${SRC_DIR}" ]; then
  echo "E2E_GATE_WORKDIR resolves to the source dir itself (${SRC_DIR}) — refusing to rsync a directory onto itself" >&2
  exit 2
fi

GATE_PROJECT="pointer-e2e-gate"
API_PORT=8091
DB_PORT=5434
MAILPIT_HTTP_PORT=8026
MAILPIT_SMTP_PORT=1026
VERDACCIO_PORT=4874

OVERRIDE_FILE="${SCRATCH}/docker-compose.e2e-gate.override.yaml"
BASE_COMPOSE_FILE="${SCRATCH}/docker-compose.yaml"
# Absolute paths: several scripts under e2e/ shell out to a bare `docker compose exec ...` with no
# `-f` of their own (e.g. scripts/lib/verify-email.mjs, api/key-rotation.spec.mjs) and run from
# different working directories (repo root vs e2e/) depending on the caller. A relative COMPOSE_FILE
# only resolves from the one cwd it was written for; absolute paths work from any of them, and
# Compose also uses the first file's directory as its "project directory" from these, which keeps
# `env_file: .env` resolving to the scratch copy's own .env regardless of cwd too.
COMPOSE_FILES_LIST="${BASE_COMPOSE_FILE}:${OVERRIDE_FILE}"

echo "==> Source:   ${SRC_DIR}"
echo "==> Scratch:  ${SCRATCH}"
echo "==> Project:  ${GATE_PROJECT}  (API :${API_PORT} · db :${DB_PORT} · mailpit :${MAILPIT_HTTP_PORT}/:${MAILPIT_SMTP_PORT} · verdaccio :${VERDACCIO_PORT})"

# A handful of widget specs (e.g. widget/privacy-snapshot.spec.ts) spawn their own static fixture
# server directly (not via docker) and deliberately REUSE one already listening on its port — handy
# for interactive dev, but a test process that crashes or times out before its own `afterAll` runs
# leaves that child as an orphan. The NEXT gate run then reuses it too, silently serving whatever
# fixture content (and whatever port-rewrite behaviour) the PREVIOUS run's scratch copy had —
# Observed exactly this: a stale server from an earlier run answered every later run's "already up?"
# probe. Scoped to processes rooted in OUR scratch copy — never anything outside it.
pkill -f "${SCRATCH}/e2e/scripts/serve-dir.mjs" 2>/dev/null || true
pkill -f "${SCRATCH}/e2e/fixture-app/serve.mjs" 2>/dev/null || true
pkill -f "${SCRATCH}/e2e/fixture-app/csp-nonce/serve.mjs" 2>/dev/null || true

echo "==> rsync ${SRC_DIR}/ -> ${SCRATCH}/"
mkdir -p "${SCRATCH}"
# Excludes beyond .git/node_modules/bin/obj: e2e/state, e2e/test-results, e2e/playwright-report and
# .pointer are e2e's OWN gitignored generated output (e2e/.gitignore, .gitignore) — a genuine CI
# checkout (actions/checkout@v4) never has any of it, but SRC_DIR here is a developer's real working
# copy, which can (2026-09-23: did, on this machine — a `state/upgrade.json` left by an unrelated
# manual `upgrade-job.mjs` run against the SHARED dev stack days earlier). Copying that over made
# api/upgrade.spec.mjs's R1-06-02/R1-09-02 read stale evidence of an upgrade this run never
# performed and fail against a database that doesn't hold that legacy key, instead of the clean-slate
# behavior CI always gets. Excluding them here (scratch regenerates its own, phase by phase) is what
# keeps this rsync-a-working-copy approach equivalent to CI's real fresh clone.
rsync -a --delete \
  --exclude='.git' \
  --exclude='node_modules' \
  --exclude='bin' \
  --exclude='obj' \
  --exclude='e2e/state' \
  --exclude='e2e/test-results' \
  --exclude='e2e/playwright-report' \
  --exclude='.pointer' \
  "${SRC_DIR}/" "${SCRATCH}/"
# rsync --delete does not touch --exclude'd paths at all (that is exactly why it leaves the
# scratch copy's .git alone across runs, per the comment below) — so a scratch dir reused from a
# PRIOR gate run keeps whatever it last generated under these paths unless removed explicitly here.
rm -rf "${SCRATCH}/e2e/state" "${SCRATCH}/e2e/test-results" "${SCRATCH}/e2e/playwright-report" "${SCRATCH}/.pointer"
mkdir -p "${SCRATCH}/e2e/state"
touch "${SCRATCH}/e2e/state/.gitkeep"

# `.git` was excluded above (rsync's own default `--exclude` protects an existing destination
# path from `--delete` too, so this only runs once — the scratch copy keeps this same baseline
# commit across every later run). Without SOME `.git` here, cli/registry.spec.mjs's R1-04-06
# fails outright on `git status --porcelain cli/` ("fatal: not a git repository") — it diffs
# `cli/` before/after a scratch publish to prove that publish step touched nothing, a check that
# only needs A baseline to diff against, not the real repo's actual history.
if [ ! -e "${SCRATCH}/.git" ]; then
  echo "==> Initializing a throwaway git repo in the scratch copy (for git-status-dependent specs)"
  git -C "${SCRATCH}" init -q
  git -C "${SCRATCH}" config user.email "e2e-gate@pointer.local"
  git -C "${SCRATCH}" config user.name "e2e-gate"
  git -C "${SCRATCH}" add -A
  git -C "${SCRATCH}" commit -q -m "e2e gate scratch snapshot" --no-verify
fi

# Created AFTER rsync --delete, deliberately: rsync --delete removes anything in the destination
# that isn't in the source, and the source repo has no `.gate-logs` — creating this first and
# rsyncing second silently deleted it every run.
LOG_DIR="${SCRATCH}/.gate-logs"
mkdir -p "${LOG_DIR}"
RUN_LOG="${LOG_DIR}/run-e2e.log"

echo "==> Writing compose override (${OVERRIDE_FILE})"
cat > "${OVERRIDE_FILE}" <<EOF
# Generated by scripts/local-e2e-gate.sh — do not hand-edit, do not commit alongside real changes.
# \`!override\` replaces the base file's \`ports:\` list instead of appending to it: a plain multi-file
# merge concatenates the two lists, which re-publishes the shared dev stack's ports too and collides
# with it if that stack happens to be up.
services:
  db:
    ports: !override
      - "${DB_PORT}:5432"
  api:
    ports: !override
      - "${API_PORT}:8080"
    environment:
      # Route outbound mail to THIS stack's Mailpit (internal container port unchanged — only the
      # host-published port moved). Also written into the scratch .env below for parity with
      # .github/workflows/e2e.yml's "Create dev .env" step; whichever wins, both agree.
      Email__Provider: smtp
      Email__Smtp__Host: mailpit
      Email__Smtp__Port: "1025"
      Email__FromEmail: dev@pointer.local
      Email__FromName: "Pointer (e2e gate)"
  mailpit:
    ports: !override
      - "${MAILPIT_HTTP_PORT}:8025"
      - "${MAILPIT_SMTP_PORT}:1025"
  verdaccio:
    ports: !override
      - "${VERDACCIO_PORT}:4873"
EOF

echo "==> Ensuring scratch .env"
if [ ! -f "${SCRATCH}/.env" ]; then
  cp "${SCRATCH}/.env.example" "${SCRATCH}/.env"
fi
# Idempotent — mirrors .github/workflows/e2e.yml's "Create dev .env for the compose stack" step so
# the profile-gated verdaccio service comes up under a bare `up -d` and mail is real, not log-and-skip.
ensure_env_line() {
  local key="$1" line="$2"
  grep -qE "^${key}=" "${SCRATCH}/.env" || echo "${line}" >> "${SCRATCH}/.env"
}
ensure_env_line 'Email__Provider' 'Email__Provider=smtp'
ensure_env_line 'Email__Smtp__Host' 'Email__Smtp__Host=mailpit'
ensure_env_line 'Email__Smtp__Port' 'Email__Smtp__Port=1025'
ensure_env_line 'Email__FromEmail' 'Email__FromEmail=dev@pointer.local'
ensure_env_line 'Email__FromName' 'Email__FromName=Pointer (e2e gate)'
ensure_env_line 'COMPOSE_PROFILES' 'COMPOSE_PROFILES=e2e-nightly'

if [ "${E2E_GATE_SKIP_INSTALL:-0}" != "1" ]; then
  echo "==> Installing dependencies (root, cli, e2e) — set E2E_GATE_SKIP_INSTALL=1 to skip"
  (cd "${SCRATCH}" && npm ci --no-audit --no-fund) || { echo "==> root npm ci failed" >&2; exit 1; }
  (cd "${SCRATCH}/cli" && npm ci --no-audit --no-fund && npm run build) || { echo "==> cli install/build failed" >&2; exit 1; }
  (cd "${SCRATCH}/e2e" && npm ci --no-audit --no-fund && npx playwright install --with-deps chromium) \
    || { echo "==> e2e install/playwright-install failed" >&2; exit 1; }
else
  echo "==> Skipping dependency install (E2E_GATE_SKIP_INSTALL=1)"
fi

# Git identity for the `apply`/`cli` phases (they commit into throwaway repos they create
# themselves) — exported for this process tree only, never touching global/user git config.
export GIT_AUTHOR_NAME="${GIT_AUTHOR_NAME:-$(git config --global user.name 2>/dev/null || echo e2e-gate)}"
export GIT_AUTHOR_EMAIL="${GIT_AUTHOR_EMAIL:-$(git config --global user.email 2>/dev/null || echo e2e-gate@pointer.local)}"
export GIT_COMMITTER_NAME="${GIT_AUTHOR_NAME}"
export GIT_COMMITTER_EMAIL="${GIT_AUTHOR_EMAIL}"

# --- Isolation env -----------------------------------------------------------------------------
# E2E_COMPOSE_PROJECT / E2E_COMPOSE_FILES: honoured explicitly by e2e/scripts/reset.sh, the
# run-e2e.sh `registry` phase, and scripts/restart-api.mjs (all patched to default to today's bare
# `docker compose` behaviour when these are unset — i.e. CI is untouched).
#
# COMPOSE_PROJECT_NAME / COMPOSE_FILE: the *native* Docker Compose env vars, read automatically by
# every OTHER bare `docker compose ...` call in this codebase (scripts/lib/verify-email.mjs,
# scripts/lib/docker.mjs, scripts/upgrade-assert.mjs, scripts/assert-origin-default.mjs,
# api/key-rotation.spec.mjs, api/key-store.spec.mjs, api/api-keys.spec.mjs,
# api/tenant-invite-nightly.spec.mjs, …) that this script does not — and should not have to —
# patch one by one. Exported here only, so the shared dev stack's own `docker compose` in any other
# shell is never affected.
export E2E_COMPOSE_PROJECT="${GATE_PROJECT}"
export E2E_COMPOSE_FILES="${COMPOSE_FILES_LIST}"
export COMPOSE_PROJECT_NAME="${GATE_PROJECT}"
export COMPOSE_FILE="${COMPOSE_FILES_LIST}"

export E2E_API_URL="http://localhost:${API_PORT}"
export E2E_BASE_URL="http://localhost:${API_PORT}"        # scripts/upgrade-job.mjs (LEGACY_REF path; unused without it)
# Deliberately NOT exporting POINTER_SERVER ambiently: scripts/lib/init-args.mjs now derives its
# SERVER constant from E2E_API_URL directly (see that file), and passes POINTER_SERVER explicitly
# only into the specific CLI child processes that need it (INIT_ENV). Exporting it here too used to
# leak into cli/init.spec.mjs's own "no --server/--key given" scenario, which relies on POINTER_SERVER
# being genuinely unset to hit the CLI's baked-in production default rather than an origin some
# OTHER spec in the same run already cached a (this stack's) valid global credential for.
export E2E_FIXTURE_URL="http://localhost:4173"              # unchanged default — fixture servers run on the host directly
export VERDACCIO_URL="http://localhost:${VERDACCIO_PORT}"   # scripts/lib/registry.mjs
export E2E_MAILPIT_URL="http://localhost:${MAILPIT_HTTP_PORT}" # scripts/lib/mail.mjs
export POINTER_SWAGGER_URL="http://localhost:${API_PORT}/swagger/v1/swagger.json" # root scripts/generate-clients.mjs + publish-clients-local.mjs (cli/local-clients.spec.mjs)

# The CLI's global per-machine credential/token store (cli/src/credentials.ts, cli/src/config.ts)
# is keyed by server URL and lives under $POINTER_CONFIG_DIR (default ~/.config/pointer) — real,
# intentional persistence for a real developer's machine, but this port is REUSED by every gate
# run: without this override, a credential a previous gate run saved for http://localhost:8091
# quietly survives the `docker compose down -v` + reset + reseed between runs and gets resolved by
# a later run's CLI specs that expect NO key to be resolvable (e.g. cli/init.spec.mjs's "missing
# --key" case), which then fails against the new database with "Invalid API key" instead. Isolating
# it here — a fresh directory every run, inside the scratch copy itself — matches what a genuinely
# clean CI runner's $HOME already gives that job for free.
export POINTER_CONFIG_DIR="${SCRATCH}/.pointer-cli-config"
mkdir -p "${POINTER_CONFIG_DIR}"

teardown() {
  local rc=$?
  if [ "${E2E_GATE_KEEP:-0}" = "1" ]; then
    echo "==> E2E_GATE_KEEP=1 — leaving '${GATE_PROJECT}' running (${SCRATCH})"
    return
  fi
  echo "==> Tearing down compose project '${GATE_PROJECT}'"
  docker compose -p "${GATE_PROJECT}" -f "${BASE_COMPOSE_FILE}" -f "${OVERRIDE_FILE}" down -v --remove-orphans \
    || echo "==> teardown reported an error — check for leftover '${GATE_PROJECT}' containers/volumes manually" >&2
  # Same orphan-fixture-server risk as the pre-run cleanup above, the other end of it: a spec that
  # spawned one of these and crashed before its own afterAll never gets to kill it either.
  pkill -f "${SCRATCH}/e2e/scripts/serve-dir.mjs" 2>/dev/null || true
  pkill -f "${SCRATCH}/e2e/fixture-app/serve.mjs" 2>/dev/null || true
  pkill -f "${SCRATCH}/e2e/fixture-app/csp-nonce/serve.mjs" 2>/dev/null || true
  return $rc
}
trap teardown EXIT

# --- Opt-in AI phase (E2E_GATE_WITH_AI=1) --------------------------------------------------------
# Off by default — RUN_E2E_ARGS is exactly `--nightly --ci` unless the caller opts in, matching
# every previous invocation of this script byte-for-byte.
RUN_E2E_ARGS=(--nightly --ci)
if [ "${E2E_GATE_WITH_AI:-0}" = "1" ]; then
  RUN_E2E_ARGS+=(--with-ai)

  # e2e/ai/run-cases.mjs reads E2E_AI_TOOLS directly from its own environment (default
  # 'claude-code,opencode-glm,antigravity' if unset) — nothing to translate here, just make sure a
  # caller-provided value (e.g. `E2E_AI_TOOLS=claude-code scripts/local-e2e-gate.sh`) is genuinely
  # exported into the run-e2e.sh subshell below rather than left as a shell-local assignment that
  # `set -u` would otherwise trip on further down.
  export E2E_AI_TOOLS="${E2E_AI_TOOLS:-claude-code,opencode-glm,antigravity}"

  # The ai phase execs `claude` / `opencode` as real child processes (e2e/ai/harness.mjs). They are
  # ordinary host-PATH binaries, not anything this script installs, so resolve them against the
  # CALLING shell's PATH now and fail with a clear, upfront message rather than letting each tool
  # fail its first invocation deep inside run-cases.mjs's per-tool try/catch (which degrades to a
  # silent per-tool SKIP in report.md — fine for "not installed", misleading for "PATH got clobbered
  # by this script"). `export PATH` makes the resolution explicit rather than incidental to however
  # bash happened to inherit it.
  export PATH
  MISSING_AI_TOOLS=()
  for bin in claude opencode; do
    command -v "${bin}" >/dev/null 2>&1 || MISSING_AI_TOOLS+=("${bin}")
  done
  if [ ${#MISSING_AI_TOOLS[@]} -gt 0 ]; then
    echo "==> WARNING: E2E_GATE_WITH_AI=1 but not found on PATH: ${MISSING_AI_TOOLS[*]}" >&2
    echo "    (those tools will fail their first invocation and be SKIPPED for the rest of the ai phase — see e2e/ai/harness.mjs)" >&2
  fi

  echo "==> E2E_GATE_WITH_AI=1 — including the paid ai phase (E2E_AI_TOOLS=${E2E_AI_TOOLS})"
fi

echo "==> Running: bash run-e2e.sh ${RUN_E2E_ARGS[*]}   (isolated project '${GATE_PROJECT}')"
(
  cd "${SCRATCH}/e2e"
  bash run-e2e.sh "${RUN_E2E_ARGS[@]}"
) 2>&1 | tee "${RUN_LOG}"
RUN_CODE=${PIPESTATUS[0]}

REPORT="${SCRATCH}/e2e/state/report.md"
echo
echo "================================================================================"
echo " LOCAL E2E GATE — phase summary"
echo "================================================================================"
if [ -f "${REPORT}" ]; then
  awk '/^## Phases/{p=1} /^## Scenarios/{p=0} p' "${REPORT}"
else
  echo "(no report.md written — run-e2e.sh likely failed before scripts/lib/report.mjs init ran)"
fi
echo "================================================================================"
if [ "${RUN_CODE}" -eq 0 ]; then
  echo " GATE: PASS"
else
  echo " GATE: FAIL (run-e2e.sh exit ${RUN_CODE}) — see ${RUN_LOG} and ${REPORT}"
fi
echo "================================================================================"

exit "${RUN_CODE}"
