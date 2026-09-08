#!/bin/sh
# Pointer — install the AI skills (pointer-init + pointer-feedback) into your AI
# tool's skills directory.
#
# Self-configuring: this script is served pre-filled with your Pointer server URL,
# and the skills it downloads are pre-filled too — nothing to edit.
#
# Usage:
#   curl -fsSL <server>/install.sh | sh                       # → .claude/skills/
#   curl -fsSL <server>/install.sh | sh -s -- .cursor/rules   # custom directory
set -e

SERVER="<POINTER_SERVER>"
DIR="${1:-.claude/skills}"

echo "Pointer: installing skills from $SERVER into $DIR/"

curl -fsSL --create-dirs "$SERVER/pointer-init.md" -o "$DIR/pointer-init/SKILL.md"
echo "  ok  pointer-init      ($DIR/pointer-init/SKILL.md)   — add the widget to an app"
curl -fsSL --create-dirs "$SERVER/skill.md" -o "$DIR/pointer-feedback/SKILL.md"
echo "  ok  pointer-feedback  ($DIR/pointer-feedback/SKILL.md)   — list / apply comments"

# Also link .agents/ to the same files — a tool-agnostic convention some AI coding agents read
# from directly, so a non-Claude agent finds the skill without the .claude-specific path. A
# SYMLINK, not a copy: $DIR stays the one real location to edit/update — .agents/ just points at
# it, so the two can never drift out of sync. ".agents/<skill>/" is always exactly 2 segments
# deep, so "../../$DIR/..." reaches repo root then back down $DIR regardless of $DIR's own depth.
# Skipped when $DIR already IS .agents (custom-dir invocation) to avoid linking it to itself.
if [ "$DIR" != ".agents" ]; then
  mkdir -p .agents/pointer-init .agents/pointer-feedback
  ln -sf "../../$DIR/pointer-init/SKILL.md" .agents/pointer-init/SKILL.md
  ln -sf "../../$DIR/pointer-feedback/SKILL.md" .agents/pointer-feedback/SKILL.md
  echo "  ok  linked .agents/pointer-init/SKILL.md and .agents/pointer-feedback/SKILL.md -> $DIR/"
fi

# --- pointer.sh CLI helper -----------------------------------------------------
# One command instead of the manual resolve-config/login/fetch/filter choreography —
# see docs/AI_AGENT_TOKEN_OPTIMIZATION.md. Committable (not a secret), same as stack.json below,
# so re-running install.sh just refreshes it in place for every developer/agent via normal git.
mkdir -p .pointer
curl -fsSL "$SERVER/pointer.sh" -o .pointer/pointer.sh
chmod +x .pointer/pointer.sh
echo "  ok  pointer.sh        (.pointer/pointer.sh)   — run './.pointer/pointer.sh list'"

# --- local apply-bridge (optional, local dev only) -----------------------------
# Lets the widget trigger an already-installed AI CLI tool (Claude Code, agy, opencode/GLM) directly
# from the browser instead of opening a terminal — see bridge.mjs's own header for the security
# model. Requires Node locally; harmless to fetch even if unused (`pointer.sh serve` is opt-in).
curl -fsSL "$SERVER/bridge.mjs" -o .pointer/bridge.mjs
echo "  ok  bridge.mjs        (.pointer/bridge.mjs)   — run './.pointer/pointer.sh serve' for the widget's local Apply button"

# --- AI apply-tool credentials -------------------------------------------------
# The pointer-feedback skill authenticates with a long-lived personal API key (not
# email/password) and reads it from a gitignored .pointer/credentials.env. Scaffold
# both files now so this critical step is never forgotten:
#   credentials.env          real value (gitignored — never committed)
#   credentials.env.example  committable template documenting the key
mkdir -p .pointer

cat > .pointer/credentials.env.example <<'EOF'
# Pointer personal API key — copy to credentials.env and fill in.
# Find/copy yours from your Pointer profile page, or the dashboard's quick-start guide.
POINTER_API_KEY=ptr_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
EOF
echo "  ok  credentials.example  (.pointer/credentials.env.example)   — committable template"

if [ -f .pointer/credentials.env ]; then
  echo "  ok  credentials          (.pointer/credentials.env already exists — left untouched)"
else
  cat > .pointer/credentials.env <<'EOF'
# Pointer personal API key (gitignored — NEVER commit). Fill this in before
# pulling/applying feedback, or login will fail. Copy it from your Pointer profile
# page, or the dashboard's quick-start guide.
POINTER_API_KEY=
EOF
  echo "  ok  credentials          (.pointer/credentials.env)   — ⚠️  FILL IN POINTER_API_KEY"
fi

# Gitignore .pointer/ (secrets: credentials.env + pointer.sh's cached JWT in .token_cache) but keep
# the .example, stack.json, AND pointer.sh committable — none of those three are secrets (stack.json
# is detected frontend/backend/aiTools; pointer.sh is the CLI helper itself), and every developer/
# agent needs them via normal git, not a per-machine setup step.
touch .gitignore
grep -qxF '.pointer/' .gitignore || echo '.pointer/' >> .gitignore
grep -qxF '!.pointer/credentials.env.example' .gitignore || echo '!.pointer/credentials.env.example' >> .gitignore
grep -qxF '!.pointer/stack.json' .gitignore || echo '!.pointer/stack.json' >> .gitignore
grep -qxF '!.pointer/pointer.sh' .gitignore || echo '!.pointer/pointer.sh' >> .gitignore
grep -qxF '!.pointer/bridge.mjs' .gitignore || echo '!.pointer/bridge.mjs' >> .gitignore

echo ""
echo "Done. Next:"
echo "  1. Fill POINTER_API_KEY in .pointer/credentials.env — copy it from your Pointer profile"
echo "     page, or the dashboard's quick-start guide."
echo "  2. Run the 'pointer-init' skill in your AI tool to add the widget to your app — its last step"
echo "     detects the tech stack and writes the committable .pointer/stack.json."
echo "  3. Then just run: ./.pointer/pointer.sh list"
