#!/usr/bin/env bash
# Acceptance gate for the rebrand (see REBRANDING-PLAN.md §12.1).
#
# Greps the repo for brand occurrences of pointer|Pointer|POINTER|poitner, subtracts the
# documented allowlist, and exits non-zero with a file:line list if anything remains.
#
#   ./verify-no-pointer.sh              # run from a repo root
#   ./verify-no-pointer.sh --protected  # also assert the DOM/CSS token count is unchanged
#
# Exit 0 = clean. Exit 1 = brand occurrences remain. Exit 2 = protected tokens were damaged.
set -uo pipefail

EXCLUDES=(
  --exclude-dir=node_modules --exclude-dir=.git --exclude-dir=obj --exclude-dir=bin
  --exclude-dir=dist --exclude-dir=.angular --exclude-dir=.next --exclude-dir=coverage
  --exclude-dir=TestResults --exclude-dir=.vite --exclude-dir=.nuget
)

# ---------------------------------------------------------------- allowlist --
# 1. Web-platform vocabulary: NOT the brand. Renaming these breaks the product.
PROTECTED='pointer-events|pointer(down|up|move|cancel|over|out|enter|leave)|(got|lost)pointercapture|PointerEvent|pointerId|pointerType|pointerPressure|(set|release|has)PointerCapture|cursor-pointer|cursor: *pointer|any-pointer|pointer:(coarse|fine)|pointer-(coarse|fine)|touch-action'

# 2. Paths that are allowed to name the old brand (this plan, its reviews, its changelog).
# Includes this script itself: its PROTECTED/BRAND regexes necessarily spell the old brand.
ALLOW_PATHS='(^|/)docs/rebranding/|(^|/)(CHANGELOG-rebrand\.md|BASELINE\.txt|verify-no-pointer\.sh)[:$]'

# 3. Deliberate compatibility aliases — ONLY when tagged. An untagged alias fails the gate.
COMPAT_TAG='COMPAT: *remove'

# 4-7. Case-by-case survivors. Add ONLY what the interview approved, each with a reason.
#      Examples (uncomment if the corresponding decision was taken):
# EXTRA_ALLOW='20260827124245_ReassignPointerLandingOwnership|COMPOSE_PROJECT_NAME=pointer-api'
EXTRA_ALLOW="${EXTRA_ALLOW:-}"

# ------------------------------------------------------------------ scan -----
BRAND='pointer|Pointer|POINTER|poitner|Poitner'

raw=$(grep -rnE "$BRAND" . "${EXCLUDES[@]}" 2>/dev/null)

filtered=$(printf '%s\n' "$raw" \
  | grep -vE "$ALLOW_PATHS" \
  | grep -vE "$COMPAT_TAG" \
  | { [ -n "$EXTRA_ALLOW" ] && grep -vE "$EXTRA_ALLOW" || cat; } \
  | awk -v prot="$PROTECTED" '
      # Drop a line only if EVERY brand hit on it is a protected token.
      {
        line = $0
        stripped = line
        gsub(prot, "", stripped)          # remove protected tokens
        if (stripped ~ /pointer|Pointer|POINTER|poitner|Poitner/) print line
      }')

count=$(printf '%s' "$filtered" | grep -c . )

# ------------------------------------------------------------ protected ------
if [ "${1:-}" = "--protected" ]; then
  prot_now=$(grep -rEo "$PROTECTED" . "${EXCLUDES[@]}" 2>/dev/null | grep -c .)
  base_file="docs/rebranding/BASELINE.txt"
  if [ -f "$base_file" ]; then
    prot_base=$(grep -oE 'protected tokens: [0-9]+' "$base_file" | grep -oE '[0-9]+' | head -1)
    if [ -n "${prot_base:-}" ] && [ "$prot_now" -lt "$prot_base" ]; then
      echo "FAIL: protected DOM/CSS tokens dropped from $prot_base to $prot_now."
      echo "      You renamed a web-platform token (pointer-events / cursor-pointer / pointerdown)."
      exit 2
    fi
  fi
  echo "protected tokens: $prot_now (baseline ${prot_base:-unknown})"
fi

# --------------------------------------------------------------- report ------
if [ "$count" -eq 0 ]; then
  echo "PASS: no brand occurrences outside the allowlist."
  exit 0
fi

echo "FAIL: $count brand occurrence(s) remain:"
echo
printf '%s\n' "$filtered" | head -100
[ "$count" -gt 100 ] && echo "... and $((count - 100)) more"
echo
echo "Each line is either (a) still to be renamed, or (b) a deliberate survivor —"
echo "in which case tag it '// COMPAT: remove <date>' or add it to EXTRA_ALLOW with a reason."
exit 1
