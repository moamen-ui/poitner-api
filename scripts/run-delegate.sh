#!/bin/sh
# run-delegate.sh <tool> <model> <dir> <promptfile> <outfile> <sentinel>
# Never lets a delegated run fail silently: always writes an outfile, always writes a
# sentinel containing the verdict, and classifies quota/empty/short output explicitly.
TOOL="$1"; MODEL="$2"; DIR="$3"; PROMPT="$4"; OUT="$5"; SENT="$6"
START=$(date +%s)
if [ "$TOOL" = "opencode" ]; then
  (cd "$DIR" && opencode run -m "$MODEL" "$(cat "$PROMPT")") > "$OUT" 2>&1
else
  (cd "$DIR" && agy -p "$(cat "$PROMPT")" --model "$MODEL" --dangerously-skip-permissions --print-timeout 30m) > "$OUT" 2>&1
fi
RC=$?
# strip ANSI so the greps below are reliable
sed -i '' -E 's/\x1b\[[0-9;]*m//g' "$OUT" 2>/dev/null
SZ=$(wc -c < "$OUT" | tr -d ' ')
DUR=$(( $(date +%s) - START ))
if grep -qi 'usage limit\|rate limit\|quota' "$OUT"; then V=QUOTA
elif [ "$RC" -ne 0 ]; then V=ERROR
elif [ "$SZ" -lt 200 ]; then V=EMPTY
else V=OK
fi
printf 'verdict=%s rc=%s bytes=%s seconds=%s tool=%s model=%s\n' "$V" "$RC" "$SZ" "$DUR" "$TOOL" "$MODEL" > "$SENT"
[ "$V" = OK ] || printf '%s\n' "---- first 400 bytes ----" >> "$SENT" && head -c 400 "$OUT" >> "$SENT" 2>/dev/null
