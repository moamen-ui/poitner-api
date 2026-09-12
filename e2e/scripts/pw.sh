#!/usr/bin/env bash
# Run the Playwright specs for one phase directory — and, crucially, tell the truth when that
# directory holds no specs at all.
#
# Why this exists: run-e2e.sh used to invoke `npx playwright test api/` directly. Two defects
# combined to make the whole CI gate permanently green:
#
#   1. Playwright's positional argument is a REGEX matched against the test file's ABSOLUTE path.
#      This repo lives under `.../pointer-api/`, so the filter `api/` matched every spec in the
#      tree — the "api" phase was silently running the widget tests.
#   2. Every phase command ended in `|| true`, so no phase could ever fail the run.
#
# Fixing (2) alone turns the missing directories into hard failures ("No tests found"), which is
# equally useless: those phases are unwritten, not broken. So resolve the three states explicitly.
#
# Exit codes: 0 = specs ran and passed, 97 = no specs authored yet (reported as EMPTY, not PASS),
# anything else = specs ran and failed.
set -uo pipefail
cd "$(dirname "$0")/.."

dir="${1:?usage: pw.sh <dir> [file-regex]}"
file="${2:-}"

# Count real spec files rather than trusting the filter to find them.
#
# When a specific file is named, check for THAT file: the directory-level count reported "specs
# exist" for a phase whose one named spec did not, so Playwright exited 1 on "No tests found" and
# the phase read as FAIL — an unwritten scenario indistinguishable from a broken one.
if [ -n "$file" ]; then
  # The argument is a regex (e.g. 'whitelabel\.spec\.ts'); strip the escapes to test the path.
  plain=$(printf '%s' "$file" | sed 's/\\//g')
  if [ ! -f "${dir}/${plain}" ]; then
    echo "${dir}/${plain} not written yet — phase is unimplemented, not passing" >&2
    exit 97
  fi
else
  count=$(find "$dir" -maxdepth 1 -name '*.spec.*' 2>/dev/null | wc -l | tr -d ' ')
  if [ "$count" = "0" ]; then
    echo "no specs authored yet in ${dir}/ — phase is unimplemented, not passing" >&2
    exit 97
  fi
fi

# Anchor the filter so it can only match this directory: `(^|/)api/` cannot match `pointer-api/`,
# because the character preceding `api/` there is `-`, not `/` or start-of-string.
if [ -n "$file" ]; then
  npx playwright test "(^|/)${dir}/${file}"
else
  npx playwright test "(^|/)${dir}/"
fi
exit $?
