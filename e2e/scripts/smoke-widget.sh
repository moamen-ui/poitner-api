#!/usr/bin/env bash
# Post-deploy smoke check for the served widget.
#
# Answers one question: did this deploy actually put a usable widget behind this URL? It is meant
# to be run against production right after a deploy, from anywhere, with nothing but bash, curl and
# a sha256 tool — no repo checkout, no node.
#
# Every check prints `ok <name>` or `FAIL <name>: <reason>` and the script exits non-zero on the
# first failure, so the output names what broke rather than leaving someone to diff two pages.
#
#   bash e2e/scripts/smoke-widget.sh https://api.example.com
#
set -uo pipefail

SERVER="${1:-http://localhost:8090}"
SERVER="${SERVER%/}"

failed=0

fail() {
  echo "FAIL $1: $2" >&2
  failed=1
}

ok() {
  echo "ok $1"
}

# curl with a short, explicit budget: a deploy check must not hang a pipeline, and a server that
# takes 15s to answer is itself a failure.
fetch() {
  curl -fsS --max-time 15 "$@" 2>/dev/null
}

status_of() {
  curl -s -o /dev/null --max-time 15 -w '%{http_code}' "$1" 2>/dev/null
}

header_of() {
  # $1 url, $2 header name (case-insensitive)
  curl -s -o /dev/null -D - --max-time 15 "$1" 2>/dev/null \
    | tr -d '\r' \
    | awk -v want="$(echo "$2" | tr 'A-Z' 'a-z')" '
        { line = $0; lower = tolower(line)
          if (index(lower, want ":") == 1) { sub(/^[^:]*:[ ]*/, "", line); print line } }'
}

sha256_short() {
  # First 12 hex of the sha256 of stdin. shasum is on macOS and most Linux images; sha256sum is
  # the coreutils name. Try both rather than assuming a platform.
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -c1-12
  else
    shasum -a 256 | cut -c1-12
  fi
}

# ── 1. version-json ──────────────────────────────────────────────────────────
manifest="$(fetch "$SERVER/pointer.version.json")"
if [ -z "$manifest" ]; then
  fail version-json "could not fetch $SERVER/pointer.version.json"
  echo "aborting: every later check needs the manifest" >&2
  exit 1
fi

# The manifest carries "hash" at the top level AND once per entry in "retained". A greedy
# `.*"hash":"..."` takes the LAST of those — a previous build — and every later check then compares
# the current bytes against an old hash and fails. Cut the retained list off first, so only the
# top-level value can match.
manifest_head="$(printf '%s' "$manifest" | tr -d ' \n' | sed 's/"retained".*//')"
hash="$(printf '%s' "$manifest_head" | sed -n 's/.*"hash":"\([0-9a-f]*\)".*/\1/p')"
if [ -z "$hash" ]; then
  fail version-json "manifest has no usable \"hash\""
  exit 1
fi
ok version-json

# ── 2. banner-hash ───────────────────────────────────────────────────────────
# The served bytes must hash to the hash the manifest advertises. This is the check that catches a
# half-finished deploy: a new manifest next to the previous build's JavaScript.
served_hash="$(fetch "$SERVER/widget.js" | sha256_short)"
if [ "$served_hash" != "$hash" ]; then
  fail banner-hash "/widget.js hashes to ${served_hash:-<empty>}, manifest says $hash"
else
  ok banner-hash
fi

# ── 3. pinned-immutable ──────────────────────────────────────────────────────
pinned_status="$(status_of "$SERVER/widget.js?v=$hash")"
pinned_cc="$(header_of "$SERVER/widget.js?v=$hash" cache-control)"
if [ "$pinned_status" != "200" ]; then
  fail pinned-immutable "?v=$hash returned $pinned_status"
elif ! printf '%s' "$pinned_cc" | grep -q 'immutable'; then
  fail pinned-immutable "?v=$hash cache-control is '${pinned_cc:-<none>}', expected immutable"
else
  ok pinned-immutable
fi

# ── 4. unknown-404 ───────────────────────────────────────────────────────────
# An unknown pin must 404 rather than quietly fall back to the current build — a fallback would
# serve different bytes under a hash that promised they could never change.
unknown_status="$(status_of "$SERVER/widget.js?v=000000000000")"
if [ "$unknown_status" != "404" ]; then
  fail unknown-404 "an unknown pin returned $unknown_status, expected 404"
else
  ok unknown-404
fi

# ── 5. css ───────────────────────────────────────────────────────────────────
css_status="$(status_of "$SERVER/widget.css?v=$hash")"
if [ "$css_status" != "200" ]; then
  fail css "/widget.css?v=$hash returned $css_status"
else
  ok css
fi

# ── 6. embed ─────────────────────────────────────────────────────────────────
embed="$(fetch "$SERVER/embed.js")"
if [ -z "$embed" ]; then
  fail embed "/embed.js is empty or unreachable"
elif ! printf '%s' "$embed" | grep -q 'pointer'; then
  fail embed "/embed.js does not reference the widget"
else
  ok embed
fi

# ── 7. branding ──────────────────────────────────────────────────────────────
branding="$(fetch "$SERVER/api/branding")"
if [ -z "$branding" ]; then
  fail branding "/api/branding is empty or unreachable"
elif ! printf '%s' "$branding" | grep -q 'productName'; then
  fail branding "/api/branding carries no productName"
else
  ok branding
fi

# ── 8. meta ──────────────────────────────────────────────────────────────────
# Tolerated as a warning when the endpoint is absent: a server predating R1-04 has no /api/meta,
# and that is not a broken widget deploy. Anything else about it IS a failure.
meta_status="$(status_of "$SERVER/api/meta")"
if [ "$meta_status" = "404" ]; then
  echo "warn: /api/meta unavailable (R1-04) — widgetVersion check skipped"
elif [ "$meta_status" != "200" ]; then
  fail meta "/api/meta returned $meta_status"
else
  meta="$(fetch "$SERVER/api/meta")"
  widget_version="$(printf '%s' "$meta" | tr -d ' \n' | sed -n 's/.*"widgetVersion":"\([^"]*\)".*/\1/p')"
  if [ "$widget_version" != "$hash" ]; then
    fail meta "/api/meta widgetVersion is '${widget_version:-<none>}', manifest says $hash"
  else
    ok meta
  fi
fi

if [ "$failed" -ne 0 ]; then
  echo "smoke: FAILED against $SERVER" >&2
  exit 1
fi

echo "smoke: all checks passed against $SERVER (build $hash)"
