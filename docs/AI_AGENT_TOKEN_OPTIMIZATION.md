# AI Coding Agent Token Optimization — Architecture & Proposal

**Status:** Proposed · **Date:** 2026-09-06 · **Target:** Pointer API, `skill.md`, `install.sh`, and Host App DX

---

## 1. Executive Summary

When an AI coding agent (e.g., Google Antigravity, Claude Code, Cursor, Windsurf, Cline) interacts with Pointer for the first time in a host application (e.g., on a prompt like `"check pointer comments"` or `"apply pending pointer comments"`), the interaction currently consumes **over 550,000 tokens** across **20–25 sequential turns**, taking 1.5–2 minutes.

This document analyzes the root causes based on live session telemetry from a real host application (`bugbounty-frontend-v2`), and proposes a complete architectural solution that reduces first-use token consumption by **~96% (down to ~15,000–20,000 tokens)** and collapses turn count from **24 down to 2**.

---

## 2. Empirical Baseline Telemetry

During a live execution of the user prompt `"check pointer comments"` on `bugbounty-frontend-v2`, the internal session telemetry recorded the following:

| Metric | Measured Baseline | Ideal Target | Delta |
|---|---|---|---|
| **Total LLM Interaction Turns** | **24 turns** | **2 turns** | **-91.7%** |
| **Total Input Tokens (cumulative)** | **544,482 tokens** | **~15,000 tokens** | **-97.2%** |
| **Total Output Tokens (generation + thinking)** | **7,335 tokens** | **~800 tokens** | **-89.1%** |
| **Total Tokens Consumed** | **551,817 tokens** | **~16,000 tokens** | **-97.1%** |
| **Final Context Window** | **36,570 tokens** | **~8,500 tokens** | **-76.7%** |
| **Total Latency** | **~90 seconds** | **~5 seconds** | **-94.4%** |

### Why Did 24 Turns Cost 550K Tokens?

Agentic architectures resend the entire conversational context (system prompt, guidelines, history, prior tool requests, and tool outputs) on every single turn:
$$\text{Total Tokens} = \sum_{t=1}^{N} C_t \approx \mathcal{O}(N \times \bar{C})$$

At turn 1, the context was **8,132 tokens**. As tool results, file reads, and API dumps were returned, the context climbed to **36,570 tokens**. Over 24 turns, the model re-read over 540,000 cumulative input tokens.

---

## 3. Root Cause Analysis

### Root Cause 1: Auth Documentation Drift & Model Nuance (Cost: ~10–12 turns, ~250K tokens)
- The consumer's local `SKILL.md` was out of date: it instructed the agent to read `POINTER_EMAIL` / `POINTER_PASSWORD` and `POST /api/auth/login`.
- The repository was actually provisioned with `POINTER_API_KEY` inside `.pointer/credentials.env`.
- The agent failed login (`'Email' must not be empty`), inspected credentials, tried passing `X-Api-Key`, tried `Authorization: Bearer <POINTER_API_KEY>` directly, failed with 401s, checked git history, read `pointer-init/SKILL.md`, and finally curled the remote `https://api.pointer.moamen.work/skill.md` to discover `/api/auth/login-with-key`.
- *Auth Model Architecture:* `POINTER_API_KEY` is a personal credential on the `User` entity (`Domain/Entity/User.cs`), issuing a user-level JWT with no project scoping. `PROJECT` is passed as a separate route parameter on each project endpoint.

### Root Cause 2: Subshell State Isolation & Amnesia (Cost: ~3–4 turns)
- Every agent tool call (`run_command`) executes in an isolated subshell.
- When `skill.md` says `TOKEN=$(curl ...)`, subsequent tool calls cannot access `$TOKEN` unless the agent crafts combined multi-line scripts or writes a file.
- The agent spent multiple turns attempting to pipe and reuse tokens across independent commands.

### Root Cause 3: Verbose JSON Payloads in Tool Outputs (Cost: ~15K context tokens per turn)
- `GET /api/projects/{key}/comments` returns the complete comment entities, including:
  - Deep DOM `snapshot` HTML strings.
  - Stringified `computedStyles` dictionaries.
  - Stringified `appliedCssRules` arrays.
  - `parentInfo` DOM trees.
  - `pageContexts` containing serialized browser console logs and full network requests.
- For 10 comments, this output alone was **~45KB of raw JSON** dumped directly into stdout, permanently bloating the agent's prompt context for all remaining turns.
- For a read-only query (`"check comments"`), 95% of this payload is completely irrelevant (the model only needs ID, status, route, and comment text).

### Root Cause 4: Multi-Step Discovery Choreography (Cost: ~4–6 turns)
- `skill.md` presents a 4-step manual procedure:
  1. Find app & grep `.env` for server/project.
  2. Read credentials file.
  3. Run curl to log in.
  4. Run curl to fetch comments.
- LLMs follow instructions sequentially: they execute Step 1, wait for output, execute Step 2, wait for output, execute Step 3, wait for output. This guarantees a minimum of 4–5 turns even when everything succeeds on the first try.

### Root Cause 5: Agent Name Registration Placed Exclusively in "Step 5 (Apply-Only)"
- In `skill.md`, the tool self-identification logic (`"Register this tool"`) is nested under `## Step 5 — Apply (only when the user asks to apply)`.
- When a user asks a read-only query like `"check pointer comments"` or `"show pointer feedback"`, the agent naturally executes Steps 1 through 4 (Resolve config, Login, Fetch comments, Display summary) and **stops**. Step 5 is explicitly titled "only when the user asks to apply", so read-only prompts never reach the registration logic.
- Furthermore, `POST /api/auth/login-with-key` currently takes only `{"apiKey":"..."}`, with no parameter or header (`X-AI-Tool`) to attach the caller identity during the mandatory login handshake.

---

## 4. Proposed Architecture & Solutions

We propose a four-tier enhancement to the Pointer developer ecosystem:

```
┌─────────────────────────────────────────────────────────────┐
│ 1. Zero-Dependency Host Helper (.pointer/pointer.sh)         │  <-- 1-command execution
├─────────────────────────────────────────────────────────────┤
│ 2. Agent-Native skill.md (Composite fast-paths + JQ filters)│  <-- Low-token fallbacks
├─────────────────────────────────────────────────────────────┤
│ 3. API Light Projection (?view=summary)                     │  <-- 90% smaller payload
├─────────────────────────────────────────────────────────────┤
│ 4. Enhanced install.sh (Bundles CLI & token gitignores)     │  <-- Out-of-the-box setup
└─────────────────────────────────────────────────────────────┘
```

---

### Solution 1: Zero-Dependency Host Helper (`.pointer/pointer.sh`)

Pointer's `install.sh` should generate an executable helper script `.pointer/pointer.sh` directly in the host repository.

#### Design of `.pointer/pointer.sh`:
- **Zero dependencies:** Pure `bash`, `curl`, and `jq` (available on all dev/agent machines).
- **Auto-resolving:** Discovers `POINTER_SERVER` and `POINTER_PROJECT` across all `.env*` files or falls back to `.pointer/config.json`.
- **Transparent Token Caching:** Exchanges `POINTER_API_KEY` for a JWT and caches it in `.pointer/.token` (with TTL validation). Future invocations reuse the token with **zero login requests**.
- **CLI Commands:**
  - `./.pointer/pointer.sh list [--status=N]` — Returns only essential columns (ID, Status, Route, Comment text).
  - `./.pointer/pointer.sh get <id>` — Returns full element snapshot, selector, and styles for that **single comment** when ready to apply.
  - `./.pointer/pointer.sh apply <id> "<reply message>"` — Sends PATCH with `status=3`, author label, and reply in one shot.

#### Implementation Blueprint (`.pointer/pointer.sh`):
```bash
#!/usr/bin/env bash
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
  if [[ -n "$CLAUDE_BASE_DIR" || "$TERM_PROGRAM" == *"claude"* ]]; then echo "claude-code"; return; fi
  if [[ -d "$HOME/.gemini" || -n "$GEMINI_CLI" ]]; then echo "antigravity"; return; fi
  if [[ -d "$HOME/.cursor" || "$TERM_PROGRAM" == *"Cursor"* ]]; then echo "cursor"; return; fi
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
    QUERY=""
    [[ -n "$STATUS_PARAM" || -n "$ENV_PARAM" ]] && QUERY="?${STATUS_PARAM:+&$STATUS_PARAM}${ENV_PARAM:+&$ENV_PARAM}"
    QUERY="${QUERY/\?&/?}"
    curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/projects/$PROJECT/comments$QUERY" \
      | jq '[.data.items[] | {id: .id, status: (if .status==1 then "Open" elif .status==2 then "ReadyToApply" elif .status==3 then "Applied" elif .status==4 then "Archived" else "Other" end), environment: (if .environment==1 then "Local" elif .environment==2 then "Staging" else "Prod" end), body: .body, route: .element.route, file: .element.sourcePath}]'
    ;;
  queue)
    # Tries admin apply-queue first to get admin-authored predefined-action Prompts; falls back to status=2 comments
    ADMIN_QUEUE=$(curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/admin/projects/$PROJECT/apply-queue" 2>/dev/null || true)
    if echo "$ADMIN_QUEUE" | jq -e '.isSuccess' >/dev/null 2>&1; then
      echo "$ADMIN_QUEUE" | jq '.data'
    else
      curl -fsSL -H "Authorization: Bearer $TOKEN" "$SERVER/api/projects/$PROJECT/comments?status=2" | jq '.data'
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
  *)
    echo "Usage: $0 {list [status] [env]|queue|get <id>|apply <id> [msg]}"
    exit 1
    ;;
esac
```

#### Token Impact of Solution 1:
- Instead of 24 turns, the AI executes **one command**: `./.pointer/pointer.sh list`.
- The tool output is only ~30 lines of clean JSON (~300 tokens) instead of 45KB.
- Total task completes in **2 turns** and **under 20,000 tokens**.

#### Scoping & Dedup-Ref Architecture (V1 vs. V2):
- **For `list` (Discovery):** Dedup references (`pages`, `pageContexts`) are intentionally omitted because the agent only needs high-level triage metadata (ID, status, route, text).
- **For `get <id>` (Apply / Investigation):** The agent inspects the specific comment being worked on. `GET /api/comments/{id}` already performs `.Include(c => c.PageContextSnapshot)` and `.Include(c => c.Replies)` on the backend (`CommentService.cs:309-310`), returning a completely self-contained `CommentResponse` with full element capture, styles, and console/network logs. No client-side dictionary lookup is needed.
- **Repo Tooling Integration:** Backed by a dedicated `just test-cli` recipe in `justfile` (`bash -n API/wwwroot/pointer.sh`).

---

### Solution 2: API-Level Light Summary Projection

Currently, `GET /api/projects/{key}/comments` in `CommentsController.cs` invokes:
```csharp
var result = await commentService.ListAsync(key, filter, User.GetId());
```
`ListAsync` always constructs `CommentListItemDto` carrying full `Element` (with `ComputedStyles`, `AppliedCssRules`, `Snapshot`, `ParentInfo`) and full `Replies`.

#### Recommended API Extension:
Add a `view` parameter to `CommentFilter`:
```csharp
public record CommentFilter(
    CommentStatus? Status = null,
    EnvironmentTag? Environment = null,
    string? View = null // "summary" | "full" (default "full" for backwards compatibility)
);
```

When `filter.View == "summary"`:
- Select into a lightweight `CommentSummaryDto`:
  ```csharp
  public record CommentSummaryDto(
      int Id,
      CommentStatus Status,
      EnvironmentTag Environment,
      string Body,
      DateTime CreatedAt,
      string? Route,
      string? SourcePath,
      string? AuthorName
  );
  ```
- **Performance benefit:** Avoids EF Core hydration of heavy JSONB columns (`element`, `computed_styles`, `applied_css_rules`) and excludes `page_contexts`.
- **Network benefit:** Payload drops from ~5KB/comment to ~150 bytes/comment (**97% bandwidth reduction**).

---

### Solution 3: Re-engineering `API/wwwroot/skill.md` to be "Agent-Native"

`API/wwwroot/skill.md` must be optimized for how LLMs actually read instructions and execute tools.

#### Guideline A: Fast-Path First
Place the CLI runner at the very top of Step 1:
> *"If `./.pointer/pointer.sh` exists in the repo, run `./.pointer/pointer.sh list` to check comments or `./.pointer/pointer.sh queue` to inspect the apply queue. Do not run manual curl commands."*

#### Guideline B: Strict `jq` Projections on Raw Fallback
If raw `curl` is used, **never** present `curl ... | jq '.'`. Always provide a projected snippet:
```bash
curl -s -H "Authorization: Bearer $TOKEN" "$SERVER/api/projects/$PROJECT/comments" \
  | jq '[.data.items[] | {id, status, body, route: .element.route, file: .element.sourcePath}]'
```

#### Guideline C: Composite Single-Turn Fallback
Provide a single, composite shell block that does config resolution, auth, and fetch in one shot so the agent doesn't perform 4 separate sequential turns:
```bash
bash -c '
  CRED=.pointer/credentials.env; [ -f "$CRED" ] && source "$CRED"
  SERVER=$(grep -rhE "^[A-Z_]*POINTER_SERVER=" .env* 2>/dev/null | head -1 | cut -d= -f2- | tr -d "\"'\''")
  PROJECT=$(grep -rhE "^[A-Z_]*POINTER_PROJECT=" .env* 2>/dev/null | head -1 | cut -d= -f2- | tr -d "\"'\''")
  TOKEN=$(curl -s "$SERVER/api/auth/login-with-key" -H "Content-Type: application/json" -d "{\"apiKey\":\"$POINTER_API_KEY\"}" | jq -r .data.token)
  curl -s -H "Authorization: Bearer $TOKEN" "$SERVER/api/projects/$PROJECT/comments" | jq "[.data.items[] | {id, status, body, route: .element.route, file: .element.sourcePath}]"
'
```

---

### Solution 4: Enhancing `API/wwwroot/install.sh`

Update `install.sh` to install the complete DX bundle:
1. Download `pointer-init.md` and `skill.md` (as it does now).
2. Download or generate `.pointer/pointer.sh` and run `chmod +x .pointer/pointer.sh`.
3. Add `.pointer/.token_cache` to `.gitignore`.
4. Output a clear 1-line success message instructing developers to run `./.pointer/pointer.sh list`.

---

### Solution 5: Trigger Tool Registration on First Project Call (Not in "Apply-Only")

Currently, `POST /api/projects/{key}/stack` is documented under `Step 5 — Apply`. Because `AiToolsUsed` is an entity field on `Project` (`Domain/Entity/Project.cs`) rather than `User`, project-scoped registration belongs at the project boundary:

1. **Move Registration Check in `skill.md` to Step 1.4 / Step 2:**
   - Decouple tool registration from Step 5 ("Apply only").
   - Place it immediately after resolving `PROJECT` in Step 1/2 so any tool run (listing comments or applying them) checks `.pointer/stack.json` and calls `POST /api/projects/{key}/stack` if the tool is not yet recorded.
2. **Automate in `.pointer/pointer.sh`:**
   - In the CLI helper, when executing any command (`list` or `queue`), check `.pointer/stack.json`.
   - If `AI_TOOL` is missing from `aiTools`, fire the idempotent `POST /api/projects/$PROJECT/stack` and update `.pointer/stack.json` locally in the background. Requires zero manual agent cognition or extra turns.

---

## 5. Summary Impact & ROI

| Dimension | Baseline (Unoptimized) | With Updated `skill.md` | With `.pointer/pointer.sh` Helper |
|---|---|---|---|
| **Interaction Turns** | 24 | 4–6 | **2** |
| **Total Cumulative Tokens** | 551,817 | ~75,000 | **~18,000** |
| **Payload Ingested to Context** | ~45 KB | ~1.5 KB | **~0.8 KB** |
| **User Wait Time (Latency)** | ~90s | ~18s | **~5s** |
| **Failure / Trial & Error Rate** | High (auth drift, subshell amnesia) | Low | **Zero** |
| **Token Reduction** | Baseline | **-86.4%** | **-96.7%** |

---

## 6. Implementation Checklist

- [ ] **1. API / Backend (`pointer-api`)**
  - [ ] Add `string? View` to `CommentFilter.cs`.
  - [ ] Support `view=summary` projection in `CommentService.ListAsync` returning `CommentSummaryDto`.
- [ ] **2. Static Assets & Repo Tooling (`API/wwwroot/` & `justfile`)**
  - [ ] Create `API/wwwroot/pointer.sh` (the standalone CLI helper).
  - [ ] Add `test-cli: ; bash -n API/wwwroot/pointer.sh` recipe to `justfile`.
  - [ ] Update `API/wwwroot/install.sh` to curl `pointer.sh` and make it executable.
  - [ ] Update `API/wwwroot/skill.md`:
    - Add fast-path referencing `./.pointer/pointer.sh`.
    - Replace raw curl dumps with `jq` projected payloads.
    - Provide single-turn composite bash fallback.
- [ ] **3. Automated Test Suite Validation (`e2e/`)**
  - [ ] Run test suite per `docs/E2E_TEST_PLAN.md` to ensure CLI and API changes do not regress Claude Code, OpenCode/GLM, or Antigravity test passes.
- [ ] **4. Consumer App Verification (`bugbounty-frontend-v2`)**
  - [ ] Run `curl -fsSL https://api.pointer.moamen.work/install.sh | sh`.
  - [ ] Verify `"check pointer comments"` completes in 2 turns and <20K tokens.
