---
name: pointer-feedback
description: Use when the user asks about Pointer feedback or comments on an app — e.g. "what are the pointer comments", "show pointer feedback", "any feedback on <app>", "apply pending pointer comments". Reads config from the app's .env (the *POINTER_* keys under whatever prefix the stack uses — VITE_/NEXT_PUBLIC_/REACT_APP_/none) + automation credentials, logs in to the Pointer API, fetches the feedback with curl, then lists or applies the comments. No Pointer install required.
---

# Pointer Feedback

**Pointer** collects element-level feedback on a running app. A signed-in stakeholder
(developer / PM / tester / client) clicks an element and leaves a comment; comments are stored in
the **Pointer API** (a .NET service backed by PostgreSQL), **partitioned by project** and tied to
the author's real account — never anonymous. This skill fetches and works with that feedback using
only `curl` — nothing needs to be installed locally.

Two things the user typically asks for:
- **"What are the Pointer comments?"** → list the feedback (this skill's default).
- **"Apply the pending Pointer comments"** → edit the source for each queued item (section 5).

> **Every endpoint requires auth.** Unlike the old flat-file server, the API is JWT-gated. This skill
> logs in with a dedicated **automation account** (a `Developer`-role user an admin created in the
> Pointer admin UI) and sends `Authorization: Bearer <token>` on every call.

---

## Step 1 — Resolve config

Pointer is wired into an app via an **env-gated inline snippet** in `index.html`; its config lives in
that app's `.env` (Vite vars). The automation **credentials** are NOT Vite vars (they must never reach
the browser) — read them from the shell environment or a gitignored local file. **Do not hardcode.**

1. **Find the app.** The env-var **prefix is stack-specific** (`VITE_`, `NEXT_PUBLIC_`, `REACT_APP_`,
   or none) — so match the `*POINTER_SERVER` key by suffix, not a fixed prefix:
   ```bash
   grep -rlE "[A-Z_]*POINTER_SERVER=" apps/*/.env 2>/dev/null || grep -rl "pointer-feedback" apps/*/index.html
   ```
   Let `APP_DIR` be that app's directory (e.g. `apps/my-app`).

2. **Read server + project** from `$APP_DIR/.env`, matching whatever prefix the stack uses:
   ```bash
   # grab the value of the first var whose name ends with the given suffix (any prefix)
   envval(){ grep -E "^[A-Z_]*$1=" "$APP_DIR/.env" | head -1 | cut -d= -f2- | tr -d "'\""; }
   SERVER=$(envval POINTER_SERVER)        # e.g. http://localhost:8090
   PROJECT=$(envval POINTER_PROJECT)      # e.g. my-app
   ```
   - This works for `VITE_POINTER_SERVER`, `NEXT_PUBLIC_POINTER_SERVER`, `REACT_APP_POINTER_SERVER`,
     or a bare `POINTER_SERVER` alike. For Angular (no `.env`), read the value from
     `src/environments/environment*.ts` instead.
   - If `PROJECT` is empty, fall back to the `project` in the inline snippet
     (`grep -oE 'setAttribute\("project", *"[^"]+"' "$APP_DIR/index.html"`) or the app dir name.
   - If `SERVER` is empty, ask the user for the Pointer server URL.

3. **Read automation credentials.** These must NOT live in the app `.env` (it's Vite-loaded and is
   often git-tracked). Read them from a **gitignored `.pointer/credentials.env`** at the repo root,
   falling back to the shell environment. **Never commit or hardcode them.**
   ```bash
   # repo-root .pointer/credentials.env (gitignored) — KEY=VALUE lines:
   #   POINTER_EMAIL=automation@pointer.local
   #   POINTER_PASSWORD=...
   CRED=.pointer/credentials.env
   [ -f "$CRED" ] && { set -a; . "$CRED"; set +a; }
   POINTER_EMAIL="${POINTER_EMAIL:?set POINTER_EMAIL in .pointer/credentials.env or the shell}"
   POINTER_PASSWORD="${POINTER_PASSWORD:?set POINTER_PASSWORD in .pointer/credentials.env or the shell}"
   ```
   The account is a Pointer user an admin created in the dashboard (any role works for fetch/apply; a
   dedicated `Developer`-role "automation" user is conventional). If `.pointer/` isn't gitignored yet,
   add it: `echo '.pointer/' >> .gitignore`.

You now have `SERVER`, `PROJECT`, `POINTER_EMAIL`, `POINTER_PASSWORD`.

---

## Step 2 — Log in (once) and capture the token

```bash
TOKEN=$(curl -s "$SERVER/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"'"$POINTER_EMAIL"'","password":"'"$POINTER_PASSWORD"'"}' \
  | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[ -z "$TOKEN" ] && echo "Login failed — check POINTER_EMAIL/POINTER_PASSWORD and that $SERVER is up" && exit 1
AUTH=(-H "Authorization: Bearer $TOKEN")
```
(If `jq` is available, prefer `... | jq -r .data.token`.)

The API wraps every response in an envelope: `{ "isSuccess": bool, "message": string|null, "data": ... }`.
Login's `data` is `{ token, user }`.

---

## Step 3 — Fetch the comments

Status is an **int**: `1 = Open`, `2 = ReadyToApply`, `3 = Applied`. Environment: `1=Local, 2=Staging, 3=Production`.

- **All comments** for the project (for "what are the comments?"):
  ```bash
  curl -s "${AUTH[@]}" "$SERVER/api/projects/$PROJECT/comments"
  ```
- **Only the queue to apply** (status = ReadyToApply):
  ```bash
  curl -s "${AUTH[@]}" "$SERVER/api/projects/$PROJECT/comments?status=2"
  ```
- Optional filter: `&environment=2`.

The list lives at `data.items` (paged: `data.pagination`). **Each item is self-contained** — it carries
its full `element` capture, so you never need a second request to apply it.

---

## Step 4 — Show the comments

Parse `data.items` and present a compact list. For each comment show: number, `body` (the text),
`status` (1/2/3 → open / ready-to-apply / applied), `environment`, `createdAt`, the
`element.sourcePath` (file:line of the element), and any `replies`.

Shape of one item (note the nested camelCase `element`; the heavier capture fields are **JSON strings**):
```json
{ "id": 12, "status": 2, "environment": 2, "body": "make it primary",
  "authorId": "0b3f…", "createdAt": "2026-06-23T…", "appliedByLabel": null,
  "element": {
    "selector": "section > div:nth-of-type(2) > button",
    "snapshot": "<button class=\"border border-primary-500 …\">Join</button>",
    "classes": "[\"border\",\"border-primary-500\",\"text-primary-500\"]",
    "computedStyles": "{\"color\":\"…\"}",
    "appliedCssRules": "[{\"selector\":\"…\",\"styles\":\"…\"}]",
    "sourcePath": "my-app/src/components/Header.tsx:42",
    "parentInfo": "{\"tag\":\"div\",\"classes\":[…]}"
  },
  "replies": [ … ] }
```
`element.classes` / `computedStyles` / `appliedCssRules` / `parentInfo` are **stringified JSON** —
`JSON.parse` them (or read as text) before using.

---

## Step 5 — Apply (only when the user asks to apply)

For each item from the `status=2` queue:

1. **Locate the source.**
   - If `element.sourcePath` is present, open it. Try it relative to the repo root first; if not found
     and the repo has an `apps/` dir (Nx/monorepo), try `apps/<sourcePath>` — many setups emit paths
     relative to `/apps/`.
   - If absent, find the source by searching for the `element.snapshot` text or the parsed
     `element.classes`.
2. **Make the change** the comment asks for.
   - **Tailwind apps:** the visible styling is in the element's `className`. Use `element.classes` /
     `element.snapshot` to find the element and edit the classes (e.g. "make it primary" → swap the
     outline classes `border border-primary-500 text-primary-500` for the filled variant
     `bg-primary-500 text-white`). `appliedCssRules` is usually just `*` and not useful for Tailwind.
   - **Plain CSS/SCSS apps — sacred CSS rule:** edit the rule that *actually wins* on the element
     (read parsed `element.appliedCssRules`) — never invent a new, more-specific selector that could be
     overridden. That winning rule often lives in an external `.css`/`.scss`/CSS-module the AI must find
     by search.
3. **Mark it applied** so the server moves it out of the queue. `appliedByLabel` makes the apply
   human-traceable even though the JWT identity is the automation account:
   ```bash
   APPLIED_BY=$(git config user.email 2>/dev/null || echo "ai-automation")
   curl -s "${AUTH[@]}" -X PATCH "$SERVER/api/comments/<id>" \
     -H 'Content-Type: application/json' \
     -d '{"status":3,
          "reply":"Applied ✓ — <what changed and where>",
          "appliedByLabel":"'"$APPLIED_BY"'"}'
   ```
   The PATCH both flips status → `Applied` (records `appliedAt`/`appliedBy`) and appends your reply in
   one call.
4. The app's dev server (Vite HMR) reflects the change live — no manual reload.

---

## Notes

- This skill needs no Pointer clone or CLI — only `curl`. The Pointer **API** is the only instance.
- Config source of truth: the app's `.env` (the `*POINTER_*` keys, under whatever prefix the stack
  uses — `VITE_`, `NEXT_PUBLIC_`, `REACT_APP_`, or none) for server/project; shell env for the
  automation account credentials (keep them out of any committed/client-exposed file).
- The token expires (default 12h). On a `401`, just re-run Step 2.
- This file was installed by fetching `<server>/skill.md` into `.claude/skills/pointer-feedback/SKILL.md`
  and is yours to edit — tweak formatting, defaults, or apply rules to fit this repo.
