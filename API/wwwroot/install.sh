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
# The pointer-feedback skill logs in with a Pointer account and reads the
# credentials from a gitignored .pointer/credentials.env. Scaffold both files now
# so this critical step is never forgotten:
#   credentials.env          real values (gitignored — never committed)
#   credentials.env.example  committable template documenting the keys
mkdir -p .pointer

cat > .pointer/credentials.env.example <<'EOF'
# Pointer automation account — copy to credentials.env and fill in.
# Any Pointer user works for fetch/apply; a dedicated Developer-role
# "automation" user (created in the Pointer dashboard) is conventional.
POINTER_EMAIL=automation@example.com
POINTER_PASSWORD=
EOF
echo "  ok  credentials.example  (.pointer/credentials.env.example)   — committable template"

if [ -f .pointer/credentials.env ]; then
  echo "  ok  credentials          (.pointer/credentials.env already exists — left untouched)"
else
  cat > .pointer/credentials.env <<'EOF'
# Pointer automation account (gitignored — NEVER commit). Fill these in before
# pulling/applying feedback, or login will fail.
POINTER_EMAIL=
POINTER_PASSWORD=
EOF
  echo "  ok  credentials          (.pointer/credentials.env)   — ⚠️  FILL IN POINTER_EMAIL / POINTER_PASSWORD"
fi

# Gitignore .pointer/ (secrets + the CLI's pending.json) but keep the .example committable.
touch .gitignore
grep -qxF '.pointer/' .gitignore || echo '.pointer/' >> .gitignore
grep -qxF '!.pointer/credentials.env.example' .gitignore || echo '!.pointer/credentials.env.example' >> .gitignore

echo ""
echo "Done. Next:"
echo "  1. Fill POINTER_EMAIL / POINTER_PASSWORD in .pointer/credentials.env (a Pointer account)."
echo "  2. Run the 'pointer-init' skill in your AI tool to add the widget to your app."
