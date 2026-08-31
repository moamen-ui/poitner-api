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

# Gitignore .pointer/ (secrets + the CLI's pending.json) but keep the .example AND stack.json
# committable — stack.json isn't a secret (detected frontend/backend/aiTools), and every developer
# needs it via normal git, not a per-machine setup step.
touch .gitignore
grep -qxF '.pointer/' .gitignore || echo '.pointer/' >> .gitignore
grep -qxF '!.pointer/credentials.env.example' .gitignore || echo '!.pointer/credentials.env.example' >> .gitignore
grep -qxF '!.pointer/stack.json' .gitignore || echo '!.pointer/stack.json' >> .gitignore

echo ""
echo "Done. Next:"
echo "  1. Fill POINTER_API_KEY in .pointer/credentials.env — copy it from your Pointer profile"
echo "     page, or the dashboard's quick-start guide."
echo "  2. Run the 'pointer-init' skill in your AI tool to add the widget to your app — its last step"
echo "     detects the tech stack and writes the committable .pointer/stack.json."
