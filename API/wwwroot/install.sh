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

echo ""
echo "Done. Next: run the 'pointer-init' skill in your AI tool to add the widget."
