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
# Case-INSENSITIVE on purpose: an explicit list of casings misses pOinter, POinter, etc.
BRAND='pointer|poitner'

# Brand-derived residue that does NOT spell the brand. The gate is blind to these unless listed.
# Populate from the §5.11 decisions: set to '' for anything you deliberately keep.
#   pf-        widget CSS class + --pf-* theming prefix ("pointer feedback"), 175 classes
#   ptr_       API-key prefix
#   moamen\.work   the old domain
EXTRA_BRAND="${EXTRA_BRAND_PATTERN:-}"     # e.g. export EXTRA_BRAND_PATTERN='\bpf-|\bptr_|moamen\.work'

raw=$(grep -rniE "$BRAND" . "${EXCLUDES[@]}" 2>/dev/null)

drop_allowed() {
  grep -vE "$ALLOW_PATHS" \
  | grep -vE "$COMPAT_TAG" \
  | { [ -n "$EXTRA_ALLOW" ] && grep -vE "$EXTRA_ALLOW" || cat; }
}

# Pass A — occurrences that spell the brand, minus lines whose only hits are protected tokens.
# NOTE: the protected-token stripping runs in awk, which has NO \b support — the PROTECTED
# pattern above is therefore written without word boundaries on purpose.
filtered=$(printf '%s\n' "$raw" | drop_allowed \
  | awk -v prot="$PROTECTED" '
      # Drop a line only if EVERY brand hit on it is a protected token.
      {
        line = $0
        stripped = line
        gsub(prot, "", stripped)          # remove protected tokens
        if (tolower(stripped) ~ /pointer|poitner/) print line
      }')

# Pass B — brand-derived residue that does not spell the brand (pf-, ptr_, the old domain).
# grep -E handles these directly; no protected-token stripping applies.
if [ -n "$EXTRA_BRAND" ]; then
  extra_hits=$(grep -rnE "$EXTRA_BRAND" . "${EXCLUDES[@]}" 2>/dev/null | drop_allowed)
  if [ -n "$extra_hits" ]; then
    filtered="$(printf '%s\n%s' "$filtered" "$extra_hits" | grep -c . >/dev/null; printf '%s\n%s' "$filtered" "$extra_hits")"
  fi
fi

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

# ------------------------------------------------------------- filenames -----
# A content grep can never flag a brand-named FILE (and never reads binaries at all):
# e.g. pointer-ext-v0.1.0.zip, store-assets/pointer-*.jpg, Pointer.sln, pointer.js.
names=$(find . -iname '*pointer*' -o -iname '*poitner*' 2>/dev/null \
        | grep -vE 'node_modules|/\.git/|/obj/|/bin/|/dist/|\.angular|docs/rebranding|verify-no-pointer\.sh')
name_count=$(printf '%s' "$names" | grep -c . )
if [ "$name_count" -gt 0 ]; then
  echo "FAIL: $name_count path(s) still NAMED for the old brand:"
  printf '%s\n' "$names" | head -40
  echo
  count=$((count + name_count))
fi

# --------------------------------------------------------------- report ------
if [ "$count" -eq 0 ]; then
  echo "PASS: no brand occurrences outside the allowlist."
  exit 0
fi

echo "FAIL: $count brand occurrence(s) remain:"
echo
printf '%s\n' "$filtered" | grep -c . >/dev/null && printf '%s\n' "$filtered" | grep . | head -100
[ "$count" -gt 100 ] && echo "... and $((count - 100)) more"
echo
echo "Each line is either (a) still to be renamed, or (b) a deliberate survivor —"
echo "in which case tag it '// COMPAT: remove <date>' or add it to EXTRA_ALLOW with a reason."
exit 1
