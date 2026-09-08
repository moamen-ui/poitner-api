#!/usr/bin/env bash
# Pointer CLI helper for AI coding agents — installed by install.sh into a host repo's
# .pointer/pointer.sh. Wraps the same endpoints skill.md documents manually, but as one
# command instead of a 4-step curl choreography (config resolve -> login -> fetch -> filter).
# See docs/AI_AGENT_TOKEN_OPTIMIZATION.md for the design this implements.
set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# 1. Resolve Server & Project
SERVER="${POINTER_SERVER:-$(grep -rhE '^[A-Z_]*POINTER_SERVER=' "$ROOT_DIR"/.env* 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"")}"
PROJECT="${POINTER_PROJECT:-$(grep -rhE '^[A-Z_]*POINTER_PROJECT=' "$ROOT_DIR"/.env* 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"")}"
API_KEY="${POINTER_API_KEY:-$(grep -hE '^POINTER_API_KEY=' "$SCRIPT_DIR/credentials.env" 2>/dev/null | head -1 | cut -d= -f2- | tr -d "'\"")}"

if [[ -z "$SERVER" || -z "$PROJECT" || -z "$API_KEY" ]]; then
  echo "Error: Missing configuration in .env or .pointer/credentials.env" >&2
  exit 1
fi

TOKEN_FILE="$SCRIPT_DIR/.token_cache"

get_token() {
  if [[ -f "$TOKEN_FILE" ]]; then
    cat "$TOKEN_FILE"
    return
  fi
  local token
  token=$(curl -fsSL "$SERVER/api/auth/login-with-key" \
    -H 'Content-Type: application/json' \
    -d "{\"apiKey\":\"$API_KEY\"}" | jq -r '.data.token // .token')
  echo "$token" > "$TOKEN_FILE"
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

# Auto-registers tool identity in .pointer/stack.json and server if not yet recorded (idempotent)
ensure_tool_registered() {
  local token="$1"
  local tool
  tool=$(detect_ai_tool)
  local stack_file="$SCRIPT_DIR/stack.json"
  if [[ -f "$stack_file" ]] && jq -e --arg t "$tool" '.aiTools // [] | index($t)' "$stack_file" >/dev/null 2>&1; then
    return
  fi
  local res
  res=$(curl -fsSL -X POST -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    "$SERVER/api/projects/$PROJECT/stack" -d "{\"aiTool\":\"$tool\"}" 2>/dev/null || true)
  if echo "$res" | jq -e '.isSuccess' >/dev/null 2>&1; then
    echo "$res" | jq '.data' > "$stack_file"
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
    AUTHOR=$(git config user.email 2>/dev/null || echo "ai-agent")
    curl -fsSL -X PATCH -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
      "$SERVER/api/comments/$ID" \
      -d "{\"status\":3,\"reply\":\"$MSG\",\"appliedByLabel\":\"$AUTHOR\"}" | jq '.data'
    ;;
  serve)
    # Starts the local apply-bridge (bridge.mjs) so the widget can trigger an already-installed AI
    # CLI tool directly from the browser, instead of the developer opening a terminal for it. Local
    # dev only — binds 127.0.0.1, see bridge.mjs's own header for the security model.
    command -v node >/dev/null 2>&1 || { echo "Error: node is required to run 'serve' (bridge.mjs)" >&2; exit 1; }
    exec node "$SCRIPT_DIR/bridge.mjs" "${2:-4772}"
    ;;
  *)
    echo "Usage: $0 {list [status] [env]|queue|get <id>|apply <id> [msg]|serve [port]}"
    exit 1
    ;;
esac
