#!/usr/bin/env bash
# pointer-skill-version: <POINTER_SKILL_VERSION>
# Pointer CLI helper for AI coding agents — installed by install.sh into a host repo's
# .pointer/pointer.sh. Wraps the same endpoints skill.md documents manually, but as one
# command instead of a 4-step curl choreography (config resolve -> login -> fetch -> filter).
# See docs/AI_AGENT_TOKEN_OPTIMIZATION.md for the design this implements.
set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
CONFIG_FILE="$SCRIPT_DIR/config.json"

# A stale pre-global-store cache: the JWT cache moved to $CACHE_DIR below (keyed by server+key,
# not by repo), so an old .pointer/.token_cache left over from a previous CLI version is dead
# weight — remove it best-effort, same as the Node CLI does (see credentials.ts).
rm -f "$SCRIPT_DIR/.token_cache" 2>/dev/null || true

# The same per-machine directory the Node CLI's global credential store/cache live in (see
# cli/src/credentials.ts): $POINTER_CONFIG_DIR overrides both, for tests — never referenced with
# a trailing /pointer suffix when set, exactly like globalConfigDir()/globalCacheDir() there.
if [[ -n "${POINTER_CONFIG_DIR:-}" ]]; then
  GLOBAL_CONFIG_DIR="$POINTER_CONFIG_DIR"
  GLOBAL_CACHE_DIR="$POINTER_CONFIG_DIR/cache"
else
  GLOBAL_CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/pointer"
  GLOBAL_CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/pointer"
fi
GLOBAL_CREDS_FILE="$GLOBAL_CONFIG_DIR/credentials.json"

# -p <key>: which Pointer project to act on, in a multi-project (monorepo) repo. Extracted from the
# argument list here, before the subcommand dispatch at the bottom, which expects the subcommand as
# $1 — so a preceding `-p <key>` (in any position) must be shifted out first. `-p=<key>` also works.
PROJECT_FLAG=""
args=()
while [ $# -gt 0 ]; do
  case "$1" in
    -p)
      PROJECT_FLAG="${2:-}"
      shift 2
      ;;
    -p=*)
      PROJECT_FLAG="${1#-p=}"
      shift
      ;;
    *)
      args+=("$1")
      shift
      ;;
  esac
done
set -- "${args[@]}"

# Is this repo's config.json multi-project (a non-empty `projects` map)? Read once, reused below
# both to resolve PROJECT and, in `ensure_tool_registered`, to pick the right stack file.
is_multi_project() {
  [[ -f "$CONFIG_FILE" ]] && jq -e '(.projects // {}) | length > 0' "$CONFIG_FILE" >/dev/null 2>&1
}

# 1. Resolve Server & Project
# Tries, in order: (a) root-level .env* (single-app repo), (b) one level of subdirectories (a
# monorepo with per-app .env, e.g. angular/.env, react/.env.production — excluding node_modules/
# dist/build), (c) .pointer/credentials.env's own POINTER_SERVER/POINTER_PROJECT lines, for a repo
# with no matching .env anywhere (e.g. an Angular app whose config lives in TypeScript
# environment.ts, not .env) — the same file POINTER_API_KEY below already lives in.
#
# Every lookup below ends in `|| true`: under `set -eo pipefail`, a pipeline that legitimately
# finds nothing (missing key, missing file, or — the concrete bug this fixes — an UNMATCHED GLOB,
# which bash passes through as its own literal string when nothing expands it, making grep fail
# with a real "No such file" error) would otherwise silently kill the WHOLE SCRIPT the instant any
# one optional lookup comes up empty, before ever falling through to the next source or reaching
# the "missing configuration" check below. Confirmed live: a repo with no root .env at all (e.g.
# this monorepo layout) died here with no error message until this fix.
resolve_config() {
  local suffix="$1" val=""
  local root_env_files=("$ROOT_DIR"/.env*)
  # Guard on the glob having actually matched a real file — an unmatched glob's sole "element" is
  # the literal, unexpanded pattern string, which -e correctly reports as not existing.
  if [[ -e "${root_env_files[0]}" ]]; then
    val=$(grep -rhE "^[A-Z_]*${suffix}=" "${root_env_files[@]}" 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"" || true)
  fi
  if [[ -z "$val" ]]; then
    # maxdepth 3 (not 2): covers both a one-level monorepo (angular/.env, react/.env — this
    # repo's own shape) AND skill.md's own documented two-level Nx/monorepo convention
    # (apps/myapp/.env), whose .env file sits 3 path components below ROOT_DIR, not 2 — confirmed
    # live that maxdepth 2 silently missed the latter.
    val=$(find "$ROOT_DIR" -maxdepth 3 -type f -iname ".env*" \
      -not -path '*/node_modules/*' -not -path '*/dist/*' -not -path '*/build/*' 2>/dev/null \
      -print0 | xargs -0 grep -hE "^[A-Z_]*${suffix}=" 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"" || true)
  fi
  if [[ -z "$val" ]]; then
    val=$(grep -hE "^${suffix}=" "$SCRIPT_DIR/credentials.env" 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"" || true)
  fi
  echo "$val"
}

SERVER="${POINTER_SERVER:-$(resolve_config POINTER_SERVER)}"

# Resolution order: -p flag -> POINTER_PROJECT env/credentials.env line -> for a multi-project
# repo with neither, print the configured keys and stop (there is no single default to fall back
# to) -> the single-project .env/credentials.env fallback `resolve_config` already implements.
if [[ -n "$PROJECT_FLAG" ]]; then
  PROJECT="$PROJECT_FLAG"
elif [[ -n "$POINTER_PROJECT" ]]; then
  PROJECT="$POINTER_PROJECT"
elif is_multi_project; then
  KEYS=$(jq -r '(.projects // {}) | keys | join(", ")' "$CONFIG_FILE" 2>/dev/null)
  echo "Several projects configured — pass -p <key> (one of: $KEYS)" >&2
  exit 2
else
  PROJECT=$(resolve_config POINTER_PROJECT)
fi

# API key resolution order: POINTER_API_KEY env -> repo .pointer/credentials.env -> the global
# per-machine store `pointer login` (or a first `init`) writes, keyed by server origin — same
# order and same store the Node CLI's resolveApiKey (credentials.ts) implements.
API_KEY="${POINTER_API_KEY:-}"
if [[ -z "$API_KEY" ]]; then
  API_KEY=$(grep -hE '^POINTER_API_KEY=' "$SCRIPT_DIR/credentials.env" 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"" || true)
fi
if [[ -z "$API_KEY" && -f "$GLOBAL_CREDS_FILE" ]]; then
  # Keyed by server ORIGIN (no trailing slash) — SERVER read from .env/credentials.env is always a
  # bare origin in practice, so trimming a trailing slash is enough to match what the CLI stores.
  API_KEY=$(jq -r --arg o "${SERVER%/}" '.[$o].apiKey // empty' "$GLOBAL_CREDS_FILE" 2>/dev/null || true)
fi

if [[ -z "$SERVER" || -z "$PROJECT" || -z "$API_KEY" ]]; then
  echo "Error: Missing configuration in .env, .pointer/credentials.env, or the global store ($GLOBAL_CREDS_FILE)" >&2
  echo "Run: npx pointer-feedback login" >&2
  exit 1
fi

# The stack cache this project writes to: the single `stack.json` for a single-project repo, or
# `projects/<key>.stack.json` alongside it for one app in a multi-project repo (see cli/src/config.ts).
if is_multi_project; then
  STACK_FILE="$SCRIPT_DIR/projects/$PROJECT.stack.json"
else
  STACK_FILE="$SCRIPT_DIR/stack.json"
fi

# The cached JWT: moved off .pointer/ (a git-repo path) onto the same global per-machine cache
# directory the Node CLI uses (tokenCacheFile in credentials.ts) — keyed by server+key rather than
# by repo, so a JWT survives a fresh clone and is shared across every repo authenticated the same
# way. cksum is POSIX and needs no extra dependency beyond what this script already requires.
mkdir -p "$GLOBAL_CACHE_DIR" 2>/dev/null || true
CACHE_KEY=$(printf '%s' "${SERVER}:${API_KEY}" | cksum | awk '{print $1}')
TOKEN_FILE="$GLOBAL_CACHE_DIR/${CACHE_KEY}.json"

get_token() {
  if [[ -f "$TOKEN_FILE" ]]; then
    local cached
    cached=$(jq -r '.token // empty' "$TOKEN_FILE" 2>/dev/null || true)
    if [[ -n "$cached" ]]; then
      echo "$cached"
      return
    fi
  fi
  local token
  token=$(curl -fsSL "$SERVER/api/auth/login-with-key" \
    -H 'Content-Type: application/json' \
    -d "{\"apiKey\":\"$API_KEY\"}" | jq -r '.data.token // .token')
  jq -n --arg t "$token" '{token: $t}' > "$TOKEN_FILE" 2>/dev/null || printf '{"token":"%s"}\n' "$token" > "$TOKEN_FILE"
  echo "$token"
}

detect_ai_tool() {
  if [[ -n "$POINTER_AI_TOOL" ]]; then echo "$POINTER_AI_TOOL"; return; fi
  # Real env vars a running tool sets for ITS OWN session — never a directory-existence check
  # (e.g. "~/.gemini exists") since another tool merely being INSTALLED on the machine, not the
  # one actually running right now, produces false positives there.
  if [[ -n "$CLAUDECODE" || -n "$CLAUDE_CODE_ENTRYPOINT" ]]; then echo "claude-code"; return; fi
  # NOT $AI_AGENT — verified generic, set even by this Claude Code session itself
  # ($AI_AGENT=claude-code_2-1-251_agent), so it would misclassify other tools as antigravity.
  if [[ -n "$ANTIGRAVITY_AGENT" || -n "$GEMINI_CLI" ]]; then echo "antigravity"; return; fi
  if [[ "$TERM_PROGRAM" == *"Cursor"* ]]; then echo "cursor"; return; fi
  if [[ -n "$WINDSURF" ]]; then echo "windsurf"; return; fi
  echo "other"
}

# Auto-registers tool identity in the project's stack file ($STACK_FILE) and server if not yet
# recorded (idempotent). $STACK_FILE is stack.json for a single-project repo, or
# projects/<key>.stack.json (this project's own file) for one app in a multi-project repo.
ensure_tool_registered() {
  local token="$1"
  local tool
  tool=$(detect_ai_tool)
  if [[ -f "$STACK_FILE" ]] && jq -e --arg t "$tool" '.aiTools // [] | index($t)' "$STACK_FILE" >/dev/null 2>&1; then
    return
  fi
  local res
  res=$(curl -fsSL -X POST -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    "$SERVER/api/projects/$PROJECT/stack" -d "{\"aiTool\":\"$tool\"}" 2>/dev/null || true)
  if echo "$res" | jq -e '.isSuccess' >/dev/null 2>&1; then
    mkdir -p "$(dirname "$STACK_FILE")"
    echo "$res" | jq '.data' > "$STACK_FILE"
  fi
}

TOKEN=$(get_token)
ensure_tool_registered "$TOKEN"

case "${1:-list}" in
  list|comments)
    STATUS_PARAM="${2:+status=$2}"
    ENV_PARAM="${3:+environment=$3}"
    QUERY="view=summary"
    [[ -n "$STATUS_PARAM" ]] && QUERY="$QUERY&$STATUS_PARAM"
    [[ -n "$ENV_PARAM" ]] && QUERY="$QUERY&$ENV_PARAM"
    curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/projects/$PROJECT/comments?$QUERY" \
      | jq '[.data.items[] | {id, status: (if .status==1 then "Open" elif .status==2 then "ReadyToApply" elif .status==3 then "Applied" elif .status==4 then "Archived" else "Other" end), environment: (if .environment==1 then "Local" elif .environment==2 then "Staging" else "Prod" end), body, route, file: .sourcePath}]'
    ;;
  queue)
    # Tries admin apply-queue first to get admin-authored predefined-action Prompts; falls back to
    # the lean summary projection of status=2 comments (no prompts, but still shows what's pending).
    ADMIN_QUEUE=$(curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/admin/projects/$PROJECT/apply-queue" 2>/dev/null || true)
    if echo "$ADMIN_QUEUE" | jq -e '.isSuccess' >/dev/null 2>&1; then
      # .element.pageRef is a dedup key into data.pages, not a route — resolve it. pickedActions
      # can hold 2+ entries (multi-select), so collect them into an array rather than a single
      # field (a generator inside an object literal would duplicate the whole row per prompt).
      echo "$ADMIN_QUEUE" | jq '(.data.pages // {}) as $pages | [.data.items[] | {id, status: (if .status==1 then "Open" elif .status==2 then "ReadyToApply" elif .status==3 then "Applied" elif .status==4 then "Archived" else "Other" end), environment: (if .environment==1 then "Local" elif .environment==2 then "Staging" else "Prod" end), body, prompts: [.pickedActions[]?.prompt], aiRules: [.aiRules[]? | "[\(.scope // (if .isPersonal then "Personal" else "Workspace" end)) | Priority \(.priority // (if .isPersonal then 3 else 1 end))] \(.title): \(.prompt)"], route: ($pages[.element.pageRef].route // .element.pageRef), file: .element.sourcePath}]'
    else
      curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/projects/$PROJECT/comments?status=2&view=summary" \
        | jq '[.data.items[] | {id, status: (if .status==1 then "Open" elif .status==2 then "ReadyToApply" elif .status==3 then "Applied" elif .status==4 then "Archived" else "Other" end), environment: (if .environment==1 then "Local" elif .environment==2 then "Staging" else "Prod" end), body, route, file: .sourcePath}]'
    fi
    ;;
  get)
    ID="${2:?Missing comment ID}"
    curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/comments/$ID" | jq '.data'
    ;;
  apply)
    ID="${2:?Missing comment ID}"
    MSG="${3:-Applied}"
    COMMIT_URL="${4:-}"
    AUTHOR=$(git config user.email 2>/dev/null || echo "ai-agent")
    curl -fsSL -X PATCH -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
      "$SERVER/api/comments/$ID" \
      -d "{\"status\":3,\"reply\":\"$MSG\",\"appliedByLabel\":\"$AUTHOR\",\"commitUrl\":\"$COMMIT_URL\"}" | jq '.data'
    ;;
  *)
    echo "Usage: $0 [-p <project>] {list [status] [env]|queue|get <id>|apply <id> [msg] [commitUrl]|serve [port]}"
    exit 1
    ;;
esac
