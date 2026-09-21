# Rebranding Plan — replace "Pointer" everywhere

**Status:** ready to execute · **Written:** 2026-09-08 · **Reviewed:** 2026-09-09 (see
`REVIEW-AGY.md`, `REVIEW-GLM.md`, `REVIEW-RESPONSE.md`) · **Owner:** Moamen
**Audience:** an AI coding agent (Claude Code / Antigravity / GLM) executing the rename, plus the
human approving each gate.

---

## 0. How to use this document

You are the executing agent. Read this whole file before touching anything.

**Rules of engagement**

1. **Do not write a single file until §1 (Interview) is answered and echoed back for confirmation.**
   The plan is parameterised; without the answers every replacement is a guess.
2. Work **one phase at a time**, in the given order. Each phase ends with a **Gate** — a command
   whose output you must paste back. Do not start phase N+1 with phase N's gate red.
3. **Never run a blind repo-wide `sed s/pointer/<new>/g`.** §3 explains why it breaks the product
   (147 legitimate DOM/CSS `pointer` tokens) and §7 gives the safe, token-aware replacement sets.
4. Anything in **§4 (Frozen identifiers)** must survive the rename untouched, or with the exact
   documented migration. These are load-bearing: they will silently corrupt data or log every
   customer out.
5. If reality contradicts this plan (a file moved, a count differs), **stop and report** — do not
   improvise a workaround. The inventory in §5 was measured on 2026-09-08; drift is expected but
   must be surfaced.
6. Commit per phase, on a branch, with the phase number in the message. Never force-push.
7. **Two conceptual traps, both of which make a "finished" rename look finished while it isn't:**
   §3.1 — much of the user-visible brand is **database rows and uploaded artwork**, not code, so
   editing constants renames nothing on a deployed instance; and §5.11 — a large amount of residue
   **does not spell "pointer"** (`pf-`, `ptr_`, `moamen.work`, the artwork), so no grep will find it.
   Read both before you start.
8. Every "silent failure" marked ⚠️ in this plan produces a **200 OK with wrong behaviour**, not an
   error. There is no test that catches them for you; the named verification step is the only proof.

**The honest definition of done** — read §3 first. "Zero occurrences of pointer" is achievable for
*brand* occurrences and **not** achievable literally, because the product is a DOM-interaction tool
whose source legitimately contains `pointer-events`, `cursor-pointer`, `pointerdown`, and
`PointerEvent`. The acceptance gate in §12 encodes that distinction.

---

## 1. Interview — ask the human first (BLOCKING)

Ask these questions, in one message, and wait. Then fill in §2 and **echo the completed table back
for explicit approval** before any edit.

### 1.1 Brand

| # | Question | Why it matters |
|---|---|---|
| Q1 | **New product name?** (as written in marketing copy, e.g. "Pointkit") | Drives every derived form in §2 |
| Q2 | Preferred **one-word lowercase token** for code (e.g. `pointkit`)? If the name is two words, do you want `pointkit` or `point-kit` in identifiers? | Custom-element tag, npm package, CSS prefix, DB name. A hyphen here changes ~40 identifiers |
| Q3 | **Legal/company name** to appear in LICENSE, extension listing, and email footers | Often differs from the product name |
| Q4 | Short **tagline** (≤132 chars, for the Chrome Web Store description) | Store limit is hard |
| Q4b | **How is the name written in Arabic copy?** Keep the Latin-script name inline (today's convention), or transliterate it? | The `ar.json` files embed "Pointer" inline in RTL sentences 23×/file. A transliteration must be chosen by a native speaker once, not improvised per string — and it changes 66 Arabic strings |

### 1.2 Domain, subdomains, email

| # | Question | Default if you say "same shape as today" |
|---|---|---|
| Q5 | **Apex domain purchased?** (e.g. `pointkit.dev`) | — |
| Q6 | Which **subdomains** will exist? | `api.`, `app.`, `app-angular.`, `app-react.`, `app-vue.`, `demo.`, plus apex for the landing page |
| Q7 | Will the **apex** serve the landing page, or redirect to `www`? | apex serves landing (today's behaviour) |
| Q8 | **Transactional-email sender address** (Brevo) and **From name**? | `noreply@<domain>` / the product name |
| Q9 | Any other mailboxes to create (`support@`, `hello@`, `admin@`)? | `support@` |
| Q10 | Is the Brevo sender domain **already verified** (SPF/DKIM) for the new domain? | If not, phase 10 blocks: emails will silently drop |

### 1.3 Repositories

| # | Question | Notes |
|---|---|---|
| Q11 | **Four new repo URLs** — API, dashboard, extension, widget. Give the exact `git@github.com:<org>/<repo>.git` for each | Today: `poitner-api` (typo, contains API + widget + extension + landing + e2e) and `pointer-dashboard` |
| Q12 | For each: **preserve git history** (`git remote set-url` / mirror push) or **fresh init**? | Recommended: mirror push — keeps blame and the 200+ commit history |
| Q13 | The extension and widget currently live **inside** the API repo (`extension/`, `web-component/`). Split them out with `git filter-repo` (history preserved, per-directory) or copy them in fresh? | Split changes the build: `justfile` targets and the widget→`API/wwwroot/pointer.js` build output cross the new repo boundary — see §8.5 |
| Q14 | Keep the **old repos** as archived redirects, or delete? | Archive; the VM's git remote and the publish workflow point at the old URL |

### 1.4 Packages and registry

| # | Question | Today |
|---|---|---|
| Q15 | **npm scope** for the generated clients — keep `@moamen-ui` or move to `@<newname>`? | `@moamen-ui` |
| Q16 | **Package names** for the three orval clients | `@moamen-ui/pointer-{angular,react,vue}` @ 1.0.31 |
| Q17 | Publish the new packages starting at **`1.0.0`** (clean slate) or continue at **`1.0.32`** (continuity)? | Recommended `1.0.0` — a new package name has no history to continue |
| Q18 | **Deprecate or unpublish** the old packages? | `npm deprecate` (never unpublish — it breaks anyone pinned) |
| Q19 | Same GitHub Packages registry, or npmjs.com? | GitHub Packages (`npm.pkg.github.com`), token `NODE_AUTH_TOKEN` |

### 1.5 Compatibility policy (the decisions that shape half the work)

| # | Question | Impact |
|---|---|---|
| Q20 | **Do live customer installs exist today** (pages carrying `<pointer-feedback>` + `/pointer.js`, repos carrying `.pointer/` and the `pointer-*` skills)? | If **no**: a hard rename, ~40% less work, no alias layer. If **yes**: §9 dual-support is mandatory |
| Q21 | If yes — how long must the **old contract** keep working? | Sets the deprecation removal date |
| Q22 | Rename the **Postgres database + role** (`pointer` → new), or leave them? | Rename = downtime + dump/restore or `ALTER`; the names are invisible to users. See §10.4 |
| Q23 | Rename existing **project keys** that contain "pointer" (`pointer-api`, `pointer-landing`)? | Data migration + upload-folder move + rewriting stored screenshot URLs. See §11 |
| Q24 | Is the Chrome extension **published** to the Web Store? | A published listing keeps its ID; a name change is a listing update + review. An unpublished one is free to rename |
| Q25 | Rename the misspelled **`poitner-api`** → correct spelling as part of this? | Yes — 52 `poitner` occurrences exist and are easy to miss |
| Q26 | The widget's CSS classes and theming tokens are prefixed **`pf-`** ("pointer feedback") — 175 distinct classes, 1,020 occurrences, plus a documented `--pf-*` custom-property theming API. Rename the prefix, or keep it? | Renaming is invisible internally (shadow DOM) but **breaks every customer's theme override** unless §9's fallback is used. `pf-` does not spell "pointer", so the gate will never flag it either way |
| Q27 | API keys are generated with the prefix **`ptr_`** (`ProfileService.cs:73`). New prefix, or keep? | Existing keys keep their `ptr_` prefix forever and must keep validating. Changing it only affects *newly issued* keys |
| Q28 | Today every service is a **subdomain of a shared personal domain** (`api.pointer.moamen.work`). The new brand moves to its **own apex**. Confirm? | This changes the hardcoded CORS allow-list (`API/Program.cs:61-68`), SPF/DKIM/DMARC scope, and cookie/origin assumptions — it is not just a string swap |
| Q29 | Replace the **artwork**: product logo, favicon, extension icons (16/32/48/128), and the three Chrome store images? Who produces them? | Branding stores `Logo` and `Favicon` per tenant in the DB (`BrandingResponse.cs:13,15`) — new artwork is a data + asset task, not a code one |

**Do not proceed past this section without answers to Q1, Q2, Q5, Q11, Q15, Q16, Q20.** The rest
have safe defaults noted above; if the human declines to answer, state the default you assumed.

---

## 2. Answers block — fill this in, then echo it back

```yaml
# --- brand -------------------------------------------------------------
NAME_DISPLAY:   ""      # Q1  e.g. Pointkit          (marketing copy, emails, page titles)
NAME_PASCAL:    ""      # Q1  e.g. Pointkit          (C# namespaces/assemblies: [A-Za-z][A-Za-z0-9]*)
NAME_LOWER:     ""      # Q2  e.g. pointkit          (npm, css prefix, folders, db)
NAME_KEBAB:     ""      # Q2  e.g. pointkit          (repos, element tag prefix, skill names)
NAME_UPPER:     ""      # Q2  e.g. POINTKIT          (env vars, JS globals)
NAME_SNAKE:     ""      # Q2  e.g. pointkit          (storage keys)
LEGAL_NAME:     ""      # Q3
TAGLINE:        ""      # Q4  <=132 chars
# --- domain ------------------------------------------------------------
DOMAIN:         ""      # Q5  e.g. pointkit.dev
HOST_API:       ""      #     api.$DOMAIN
HOST_APP:       ""      #     app.$DOMAIN
HOST_APP_NG:    ""      #     app-angular.$DOMAIN
HOST_APP_REACT: ""      #     app-react.$DOMAIN
HOST_APP_VUE:   ""      #     app-vue.$DOMAIN
HOST_DEMO:      ""      #     demo.$DOMAIN
HOST_LANDING:   ""      #     $DOMAIN
EMAIL_FROM:     ""      # Q8  noreply@$DOMAIN
EMAIL_FROM_NAME:""      # Q8
EMAIL_SUPPORT:  ""      # Q9
# --- repos -------------------------------------------------------------
REPO_API:       ""      # Q11
REPO_DASHBOARD: ""      # Q11
REPO_EXTENSION: ""      # Q11
REPO_WIDGET:    ""      # Q11
HISTORY_MODE:   ""      # Q12/Q13: mirror | filter-repo | fresh
# --- packages ----------------------------------------------------------
NPM_SCOPE:      ""      # Q15 e.g. @moamen-ui
PKG_ANGULAR:    ""      # Q16 e.g. @moamen-ui/pointkit-angular
PKG_REACT:      ""      # Q16
PKG_VUE:        ""      # Q16
PKG_START_VER:  ""      # Q17 e.g. 1.0.0
# --- policy ------------------------------------------------------------
LIVE_INSTALLS:  ""      # Q20 yes | no      <- the single biggest branch in this plan
COMPAT_UNTIL:   ""      # Q21 e.g. 2027-03-01 | n/a
RENAME_DB:      ""      # Q22 yes | no
RENAME_PROJECT_KEYS: "" # Q23 yes | no
EXT_PUBLISHED:  ""      # Q24 yes | no
FIX_POITNER_TYPO: ""    # Q25 yes | no
RENAME_PF_PREFIX: ""    # Q26 yes | no  (widget css classes + --pf-* theming tokens)
CSS_PREFIX:       ""    # Q26 the new short prefix, if renaming (e.g. "xy-")
API_KEY_PREFIX:   ""    # Q27 e.g. "xy_" | keep "ptr_"
DOMAIN_MODEL:     ""    # Q28 own-apex | subdomain-of-shared
ARTWORK_OWNER:    ""    # Q29 who produces logo/favicon/extension icons/store images
JWT_ISSUER_MODE:  ""    # §4.6 freeze | dual-accept   <- freeze unless you have a 3-deploy window
```

### 2.1 Derived identifiers — compute and validate all of these

| Derived | Formula | Constraint that will bite you |
|---|---|---|
| Custom element tag | `${NAME_KEBAB}-feedback` | **Must contain a hyphen**, start `[a-z]`, no uppercase (HTML spec). `pointkitfeedback` is invalid and `customElements.define` throws |
| Highlight CSS class | `${NAME_KEBAB}-feedback-hl` | Must not collide with a host app's classes — keep the two-token prefix |
| Style element id | `${NAME_KEBAB}-feedback-hl-style` | Same |
| Widget asset paths | `/${NAME_LOWER}.js`, `/${NAME_LOWER}.css` | Served from `API/wwwroot/` — file name = URL |
| CLI helper | `.${NAME_LOWER}/${NAME_LOWER}.sh` | Both the dir and the script rename together |
| Config dir in host repos | `.${NAME_LOWER}/` | Appears in customers' `.gitignore` — see §4 |
| Env var prefix | `${NAME_UPPER}_` | `[A-Z_][A-Z0-9_]*`; a leading digit is invalid in POSIX sh |
| JS window globals | `__${NAME_UPPER}_CONFIG__`, `__${NAME_UPPER}_FETCH__` | Keep the double-underscore convention |
| Storage key prefix | `${NAME_SNAKE}_` | See §4.3 — needs a **fallback read**, not a swap |
| C# namespaces | `${NAME_PASCAL}.API` etc. | Valid C# identifier; **must not collide with a BCL namespace** (`System`, `Microsoft`, `Task`, `Console`) |
| Solution/assemblies | `${NAME_PASCAL}.sln`, `${NAME_PASCAL}.{API,Application,Domain,Infrastructure,Tests}.csproj` | Assembly name change invalidates any strong-name/DLL reference; there are none today |
| Postgres db + role | `${NAME_LOWER}` | ≤63 bytes, lowercase, not a reserved word |
| Skill names | `${NAME_KEBAB}-init`, `${NAME_KEBAB}-feedback` | Claude Code skill dir + `name:` frontmatter must match, kebab-case |
| Docker containers | `${NAME_LOWER}-api-{api,db,caddy}-1` | Derived from the compose project dir name on the VM |
| VM paths | `~/${NAME_LOWER}-api`, `~/${NAME_LOWER}-dashboard` | Hardcoded in `DEPLOY.md` and the Caddy `root` directives |

### 2.2 Collision pre-check (run BEFORE any replacement)

```bash
# 1. Does the new token already appear in the codebase for unrelated reasons?
grep -rin "${NAME_LOWER}" . --exclude-dir=node_modules --exclude-dir=.git | head
# 2. Would replacement create a doubled word (e.g. "kit" -> "kitkit")?
#    Check the new token is not a substring of an existing identifier you keep.
# 3. Is the npm package name free in the target registry?
npm view "${PKG_ANGULAR}" version 2>&1 | head -1
# 4. Is the custom element name valid?
node -e 'const t=process.env.TAG; if(!/^[a-z][a-z0-9._]*-[a-z0-9._-]*$/.test(t)) throw new Error("invalid custom element name: "+t); console.log("tag ok:",t)'
```

If (1) returns hits, add them to the allowlist in §12 so the acceptance gate does not read them as
failures.

---

## 3. Definition of done — and the trap that invalidates the naive version

Measured on 2026-09-08, excluding `node_modules/`, `.git/`, `obj/`, `bin/`, `dist/`, `.angular/`:

| Token | `poitner-api` | `pointer-dashboard` |
|---|---|---|
| `pointer` | 5,676 occurrences / 450 files | 2,312 / 266 |
| `Pointer` | 3,612 / 771 files | 505 / 177 |
| `POINTER` | 409 / 65 files | 106 / 21 |
| `poitner` (the repo-name typo) | 43 / 37 files | 9 / 3 |
| **Total** | **~9,740** | **~2,930** |

**Of those, 147 must NOT be renamed** — they are web-platform vocabulary, not brand:

| Token | api | dashboard | What it is |
|---|---|---|---|
| `cursor: pointer` / `cursor-pointer` | 37 | 31 | CSS keyword / Tailwind utility |
| `pointer-events` | 29 | 25 | CSS property (the widget's overlay depends on `pointer-events: none`) |
| `pointerdown` / `pointerup` / `pointermove` / `pointercancel` | 12 | 0 | DOM events — the element-picker is built on them |
| `PointerEvent`, `pointerId`, `pointerType`, `setPointerCapture` | 13 | 0 | DOM API |

Renaming any of those breaks the picker overlay and every clickable cursor in three dashboards.
This is why §7 replaces **specific tokens**, never the bare substring.

**Done means all of:**

- [ ] `verify-no-pointer.sh` (§12, shipped next to this file) exits 0 — zero brand occurrences of
      `pointer|Pointer|POINTER|poitner` outside the documented allowlist, across all four repos.
- [ ] No file, directory, or branch **name** contains the old brand (§5.9).
- [ ] `dotnet build` + `dotnet test` green (**re-baseline the count before you start** — see §8.0).
- [ ] All three dashboards `npm run build` green; Angular unit suite green at its baseline count.
- [ ] New packages published and consumed; no app resolves an old `pointer-*` package.
- [ ] The five verification scenarios in §12.3 pass against the deployed stack.
- [ ] If `LIVE_INSTALLS=yes`: the old contract still works (§9), and no user was logged out (§4.3).

### 3.1 Half of this brand is data, not code — the runtime Branding system

Before editing anything, understand where user-visible copy actually comes from. The product has a
live, database-backed white-labeling subsystem:

- `Application/Services/Implementation/BrandingService.cs` — `DefaultProductName = "Pointer"` (line 10)
  is only a **fallback**; the real value is `settings.GetStringAsync(ISettingsService.BrandProductName, …)`
  (line 69), i.e. an `app_settings` row.
- `Application/DTOs/Branding/BrandingResponse.cs` — product name, URLs, and **`Logo`** (line 13) and
  **`Favicon`** (line 15) assets; plus `BrandingWriteDto.cs`.
- `GET /api/branding` — consumed by the landing page, all three dashboards, **and the widget**.
- A super-admin **Branding page** in each dashboard writes those rows.

**Consequences for this plan:**

1. Editing `DefaultProductName` renames almost nothing on a deployed instance — the DB row wins.
   The user-visible rename happens in §11.3 (SQL) or through the Branding admin page.
2. Conversely, most product-name strings you find in the dashboards are *fallbacks* (`productName:
   'Pointer'` in `useBranding.ts:37`, `BrandingPage.vue:100,309`) — rename them, but know that they
   are only visible when `/api/branding` is unreachable.
3. The **artwork** (logo, favicon) is uploaded data, not files in the repo. New artwork must be
   produced and uploaded (Q29); no find/replace will do it.
4. Because branding is **per-tenant**, every tenant's rows need review, not just the default one.
   `API/wwwroot/uploads/` currently holds `pointer-api` *and* `tuwaiq-clubs` — there is more than one
   tenant with stored assets.
5. The Caddyfile deliberately keeps `/api/branding` on `no-cache` (`Caddyfile:29`) so "rebrands
   propagate" — that mechanism only works if you keep that matcher correct (§10.2).

So: the rename is a **code** task (identifiers), a **data** task (branding rows + artwork), and an
**infra** task (hosts, mail, volumes). Doing only the first leaves the product still saying "Pointer"
to every user.

---

## 4. Frozen identifiers — rename these wrong and you lose data or users

### 4.1 EF Core migration IDs — **DO NOT rename the `[Migration("…")]` string**

`Infrastructure/Migrations/20260827124245_ReassignPointerLandingOwnership.{cs,Designer.cs}` carries:

```csharp
[Migration("20260827124245_ReassignPointerLandingOwnership")]
```

That string is the primary key in the `__EFMigrationsHistory` table on every deployed database.
Change it and EF sees an **unapplied migration** and re-runs it against production.

**Frozen migration IDs (grows over time — do not treat this as exhaustive without re-grepping
`Infrastructure/Migrations/*.cs` for `[Migration(`):**

| Migration ID | Landed | Deployed? |
|---|---|---|
| `20260827124245_ReassignPointerLandingOwnership` | pre-2026-09-08 | yes |
| `20260921215630_AddCommentFieldsAndWorkspaceSettings` (R4-01, `main` a1880b1..2554104; adds `workspace_settings` table and `comments.custom_fields`) | 2026-09-22 | **imminent tonight — treat as deployed as of the next prod deploy; once applied, its ID is frozen by the same rule as the row above** |

Same correction procedure applies to any row in this table: rename file + class + attribute together,
and `UPDATE "__EFMigrationsHistory"` in the same deploy, or leave the ID alone and allowlist it.

**Correct procedure** (only if you want the name gone):

1. Rename file + class → `20260827124245_Reassign${NAME_PASCAL}LandingOwnership`.
2. Keep the `[Migration(...)]` attribute value **in sync with the new class name**.
3. In the **same** deploy, before the app starts, run:
   ```sql
   UPDATE "__EFMigrationsHistory"
      SET "MigrationId" = '20260827124245_Reassign<NAME_PASCAL>LandingOwnership'
    WHERE "MigrationId" = '20260827124245_ReassignPointerLandingOwnership';
   ```
4. Verify: `dotnet ef migrations list` shows **no** pending migration against prod.

If you are not prepared to run that SQL, **leave this migration's name alone** and add it to the
allowlist. A historical migration name is not customer-visible.

### 4.2 EF model snapshot strings

`Infrastructure/Migrations/*.Designer.cs` and `ApplicationDbContextModelSnapshot.cs` contain
`modelBuilder.Entity("Pointer.Domain.Entity.X", …)` as **string literals** (~107 files).

- They are *historical snapshots* — they compile regardless, since they are strings.
- The **current** snapshot (`…ModelSnapshot.cs`) must match the renamed CLR types, or the next
  `dotnet ef migrations add` emits a spurious model-diff migration.
- Safe approach: replace the namespace token in **all** migration files (they are strings and class
  namespaces), then prove it with `dotnet ef migrations add __RenameCheck --dry-run`-equivalent:
  ```bash
  dotnet ef migrations add __RenameCheck && \
    grep -c "migrationBuilder\." Infrastructure/Migrations/*__RenameCheck.cs   # expect 0 real ops
  dotnet ef migrations remove   # ALWAYS remove the probe
  ```
  A non-empty `Up()` means the rename changed the model — stop and investigate.

### 4.3 Browser storage keys — a swap logs out every signed-in user

| App | File | Keys |
|---|---|---|
| Angular | `src/app/core/prefs/preferences.service.ts` | `pointer_admin_token`, `pointer_admin_user`, `pointer_admin_lang`, `pointer_admin_theme` |
| Angular | `src/app/features/shell/demo-panel.component.ts` | `pointer_demo`, `pointer_demo_dismissed` |
| Angular | `src/app/shared/install-guide/install-guide.service.ts` | `pointer_install_seen`, `pointer_install_suppressed`, `pointer_install_shown_session` |
| React | `src/lib/storage.ts` | `pointer_token`, `pointer_user`, `pointer_lang`, `pointer_theme` |
| Vue | `src/lib/storage.ts`, `src/lib/demoSession.ts` | same four + `pointer_demo`, `pointer_demo_dismissed` |
| Widget | `API/wwwroot/pointer.js` (built) / `web-component/src/*` | `pointer_token`, `pointer_user`, `pointer_visible`, `pointer_toolbar_pos`, `pointer_page_session_id`, `pointer_env_*` |
| Static admin | `API/wwwroot/admin/app.js` | `pointer_admin_token`, `pointer_admin_user` |
| Extension | `extension/src/shared.ts` | `pointer_via_proxy__` |

**Required pattern — migrate on read, once:**

```ts
function read(key: string): string | null {
  const v = localStorage.getItem(`${NEW_PREFIX}${key}`);
  if (v !== null) return v;
  const legacy = localStorage.getItem(`pointer_${key}`);   // one-time migration
  if (legacy !== null) {
    localStorage.setItem(`${NEW_PREFIX}${key}`, legacy);
    localStorage.removeItem(`pointer_${key}`);
  }
  return legacy;
}
```

Apply it in all three dashboards **and** the widget. Without it, deploy day = every user logged out,
every language/theme preference reset, and every dismissed demo panel reappearing.
Keep the fallback for at least one release; delete it on the `COMPAT_UNTIL` date.

### 4.4 Published npm packages

Never `npm unpublish` `@moamen-ui/pointer-{angular,react,vue}` — anything pinned to them breaks.
`npm deprecate '<pkg>@*' 'Renamed to <new pkg>'` instead.

### 4.5 Customer-side artifacts (only if `LIVE_INSTALLS=yes`)

These live in **other people's repos and pages** and you cannot edit them:

- `<pointer-feedback project="…" server="…">` in their HTML
- `<script src="https://api.…/pointer.js">`
- `.pointer/credentials.env`, `.pointer/pointer.sh`, `.pointer/bridge.mjs` + their `.gitignore` rules
- `.claude/skills/pointer-init/SKILL.md`, `.claude/skills/pointer-feedback/SKILL.md`,
  `.agents/pointer-{init,feedback}/SKILL.md` symlinks
- `VITE_POINTER_*`, `NEXT_PUBLIC_POINTER_SERVER`, `REACT_APP_POINTER_*`, bare `POINTER_*` env keys

§9 defines the dual-support layer for these. If `LIVE_INSTALLS=no`, skip §9 entirely and rename hard.

### 4.6 ⚠️ `JWT__Issuer` — renaming this 401s every live token, instantly

`docker-compose.prod.yml:33` sets `JWT__Issuer: "pointer-api"`, and
`API/Extensions/AuthenticationExtensions.cs:44-47` validates it **strictly**:

```csharp
ValidateIssuer = true,
ValidIssuer    = config["JWT:Issuer"],
ValidAudience  = config["JWT:Issuer"],     // issuer doubles as audience
```

`Infrastructure/Auth/JwtTokenService.cs:10` also hardcodes the default
(`Issuer = "pointer-api"`) and issues tokens with it as **both** `iss` and `aud` (line 35).

**Change that value and every token already in the wild is rejected on the next request** — every
signed-in dashboard user in all three apps, every widget session on every customer page, and every
AI-agent CLI session holding a token. They do not get a grace period; they get 401.

> This is precisely the failure mode that caused the dashboard's 401 request-storm crash. A mass
> 401 event is the worst possible way to start a rebrand.

**Two acceptable options:**

| Option | How | Cost |
|---|---|---|
| **A. Freeze it (recommended)** | Leave `JWT__Issuer` as `pointer-api` forever. It is an opaque string in a signed token; no user, customer, or API consumer ever sees it. Add it to the §12 allowlist with this reason | One allowlisted string |
| **B. Dual-accept, then flip** | 1. Deploy `ValidIssuers = [ "pointer-api", "<new>" ]` **and** `ValidAudiences = [ … ]` (note the plural properties) while still *issuing* the old one. 2. After that deploy is live, change the issuing value to the new one. 3. Wait out `JWT__LifetimeHours: 12` (+ margin). 4. Remove the old issuer | Three deploys, 12+ hours, and it must not be compressed into one window |

**Never** change the issuing value and the validation value in a single deploy.

### 4.7 The `Pointer` configuration section — three places, or it silently disables itself

The API self-configures its own dogfooding widget from a config section named `Pointer`:

| Place | Value |
|---|---|
| `API/appsettings.json:9-14` | `"Pointer": { "Enabled": true, "Server": "http://localhost:8090", "Project": "", "Environment": "staging" }` |
| `API/Program.cs:180` | `var pointer = app.Configuration.GetSection("Pointer");` — a **string literal** |
| `docker-compose.prod.yml:38-41` | `Pointer__Enabled`, `Pointer__Server`, `Pointer__Project: "pointer-api"`, `Pointer__Environment` |
| `API/Program.cs:188` | uses it to inject `{server}/embed.js?project=…` into Swagger |

Rename any **one** of the three without the others and `GetSection("<new>")` returns an empty
section, so `GetValue("Enabled", false)` falls back to **`false`** — the Swagger widget embed just
stops working, with no error, no log line, and no failing test. Rename all three in one commit, and
verify by loading `/swagger` and confirming the injected `embed.js` script tag is present.

Note `Pointer__Project: "pointer-api"` is a **project key** (data, §11), not just a name.

---

## 5. Inventory — every brand surface, by area

Counts are files containing a case-insensitive `pointer`, measured 2026-09-08. **R4-01 (2026-09-22,
`main` a1880b1..2554104) added files under `Application/`, `Infrastructure/`, `API/` and `Domain/`
after that measurement — counts below are now stale for those four rows; not re-measured here, see
notes appended to each row instead.**

### 5.1 `poitner-api` (.NET 8 API + widget + extension + landing + e2e)

| Area | Files | What carries the brand |
|---|---|---|
| `Application/` | 191 (stale, R4-01 added ≥3 more) | Namespaces `Pointer.Application.*` (DTOs, Services, Validators, Resources). **+R4-01:** `DTOs/Workspace/*` (workspace comment-field DTOs), `DTOs/Comment/UpdateCommentFieldsRequest.cs`, additive `customFields`/`commentFields` properties on `CommentListItemDto`, `CommentResponse`, `CommentApplyItemDto`, `CaptureConfigResponse`, `CreateCommentRequest` — brand-neutral names, namespace only |
| `Infrastructure/` | 107 (stale, R4-01 added ≥4 more) | Namespaces + **migration snapshot strings** (§4.2). **+R4-01:** `Migrations/20260921215630_AddCommentFieldsAndWorkspaceSettings.{cs,Designer.cs}` (frozen, §4.1), `Mappings/WorkspaceSettingMapping.cs` (new table `workspace_settings`: `id`, `owner_id`, `comment_field_definitions` jsonb, audit columns; unique filtered index `IX_workspace_settings_owner_id`), `Mappings/CommentMapping.cs` amended for new column `comments.custom_fields` jsonb, new `Mappings/JsonColumn.cs` converter |
| `API/` | 57 (stale, R4-01 added ≥1 more) | Namespaces `Pointer.API.*`, `Pointer.API.csproj`, `Pointer.API.http`, `Extensions/PointerUrlResolver.cs`, `wwwroot/` assets, `Program.cs` (`POINTER_SERVER`). **+R4-01:** `Controllers/Admin/WorkspaceController.cs`, route prefix `api/admin/workspace` (`GET`/`PUT` `api/admin/workspace/comment-fields`), new endpoint `PATCH api/comments/{id}/fields` on `CommentsController`, new Swagger/orval tag `Workspace` — added to `orval.config.ts` `filters.tags` (§5.7); none of this is brand-carrying by name, but it is a new API/DTO surface the dashboard-agent syncs from, and the tag-gating note in §5.7 now covers it too |
| `Tests/` | 48 | Namespaces + `Pointer.Tests.csproj` + hardcoded `*.pointer.moamen.work` URLs in 7 test files |
| `Domain/` | 32 (stale, R4-01 added ≥2 more) | Namespaces `Pointer.Domain.*`. **+R4-01:** `ValueObjects/CommentFieldDefinition.cs`, `Enums/CommentFieldType.cs` — brand-neutral names, namespace only |
| `web-component/` | 25 | **Widget source** — `define('pointer-feedback')`, `pointer-feedback-hl` class, `pointer-feedback-hl-style` id, storage keys, `__POINTER_*` globals |
| `extension/` | 19 | `manifest.json` (name "Pointer Feedback", description), `src/shared.ts`, `src/popup.ts`, README, E2E checklist, store assets, `pointer-ext-v0.1.0.zip` |
| `e2e/` | 35 | **A working replica of the customer contract** — and therefore a real gate. `fixture-app/beta/index.html:16-21` and `fixture-app/smoke/index.html:40` embed `<pointer-feedback>` + `http://localhost:8090/pointer.js`; `fixture-app/*/.env` carry `*_POINTER_*` keys; `widget/widget.spec.ts` asserts on the tag; `ai/cases/tc*.txt` are natural-language prompts naming the skills; `ai/harness.mjs` + `scripts/lib/api.mjs` drive the API; `state/scratch/*/` holds generated `.pointer/` and `.claude/skills/pointer-*` fixtures (regenerable — delete rather than edit) |
| `docs/` | 38 | Specs and plans — historical, see §5.10 |
| `clients/` | **479** | **orval-generated** — never hand-edit; regenerated in phase 6 |
| `landing/` | 2 (stale, R4-01 added 1 more) | `index.html` copy + `pointer-extension.zip`. **+R4-01:** `docs/comment-fields.html` added to `landing/docs/pages.json` — brand-neutral content per caller, not re-verified here |
| `scripts/` | 2 | `generate-clients.mjs`, `build-clients.mjs` |
| `.github/` | 1 | `workflows/publish-clients.yml` |
| Root | — | `Pointer.sln`, `Caddyfile`, `docker-compose.prod.yml`, `justfile`, `.env.prod.example`, `.gitignore` (`.pointer/*`), `AGENTS.md`, `CLAUDE.md`, `DEPLOY.md`, `README.md`, `.pointer/` |

### 5.2 `pointer-dashboard` (three apps at parity)

| App | Files | Notable |
|---|---|---|
| `angular/` | 39 | `@moamen-ui/pointer-angular` (74 import sites across all three apps), `core/pointer-dogfood/pointer-dogfood.service.ts` (dir + class + `WIDGET_TAG`), `shared/install-guide/*` (emits the install snippets), storage keys, `environment*.ts` |
| `react/` | 40 (stale — plus, per repo drift below, angular/vue retired 2026-09-15, not yet reflected in this row split) | `@moamen-ui/pointer-react`, `index.html` `<title>Pointer Admin</title>`, `.env.development`/`.env.production` (`VITE_API_BASE=https://api.pointer.moamen.work`), `src/lib/storage.ts`. **R4-01 (in progress, not yet merged as of 2026-09-22):** new Settings section "Comment fields" and i18n namespace `commentFields` — brand-neutral names; re-check on merge, since this agent has not seen the diff |
| `vue/` | 44 | `@moamen-ui/pointer-vue`, same `<title>`, same env files, `src/lib/storage.ts`, `src/lib/demoSession.ts` |
| Root | — | `AGENTS.md`, `CLAUDE.md`, `README.md`, `.gitignore` (`.pointer/`), `.pointer/` |

### 5.3 Runtime identifiers (the invisible contract)

| Kind | Value | Defined in |
|---|---|---|
| Custom element | `pointer-feedback` | `web-component/src/index.ts:8` |
| CSS class | `pointer-feedback-hl` | `web-component/src/constants.ts:3` |
| Style element id | `pointer-feedback-hl-style` | `web-component/src/dom.ts:40` |
| Window globals | `__POINTER_CONFIG__`, `__POINTER_FETCH__` | `API/wwwroot/pointer.js:498,61` |
| Env keys (host app) | `VITE_POINTER_{SERVER,PROJECT,ENV,ENABLED}`, `REACT_APP_POINTER_{…}`, `NEXT_PUBLIC_POINTER_SERVER`, bare `POINTER_{SERVER,PROJECT,ENV,ENABLED,API_KEY,AI_TOOL}` | widget, `pointer.sh`, skill docs, `.pointer/credentials.env.example` |
| Env key (server) | `POINTER_SERVER` | `API/Program.cs:193` |
| Storage keys | see §4.3 | — |
| Served assets | `/pointer.js`, `/pointer.css`, `/pointer.sh`, `/pointer-init.md`, `/skill.md`, `/install.sh`, `/bridge.mjs` | `API/wwwroot/` |
| **`/embed.js`** — *not* a static file | Generated inline in `API/Program.cs:283-319`: it hardcodes the `/pointer.js` path, the `<pointer-feedback>` tag, and a `window.__pointerEmbedded` guard flag inside a JS template string | `API/Program.cs` |
| Static-file cache special-case | `API/Program.cs:250-252` matches the widget **by filename**: `name.Equals("pointer.js")`/`("pointer.css")` → `Cache-Control: no-cache`. Rename the files without this and shipped widget fixes stop reaching customers (heuristic freshness serves stale copies for a long time) | `API/Program.cs` |
| CORS dashboard allow-list | `API/Program.cs:61-68` — a hardcoded `string[] dashboardOrigins` of five `*.pointer.moamen.work` origins gating `/api/admin/*`. Miss it and the renamed dashboards are CORS-blocked on every privileged call. Add the new origins **before** cutover and keep the old ones during transition (`Cors__ExtraDashboardOrigins` extends it per environment) | `API/Program.cs` |
| Caddy widget no-cache matcher | `Caddyfile:29` — `@widget path /pointer.js /pointer.css /embed.js /skill.md /api/branding`. The asset paths are listed **literally**; rename the assets without updating it and the new paths lose revalidation | `Caddyfile` |
| **Server-side placeholder injection** | `API/Program.cs:197-201` holds a hardcoded `injectedFiles` HashSet — `"/pointer-init.md"`, `"/skill.md"`, `"/install.sh"` — and rewrites `<POINTER_SERVER>` in their bodies to the request origin (`Program.cs:212`, via `PointerUrlResolver.ResolvePublicUrl`). Rename the wwwroot files without updating this set and the middleware stops matching: users receive the raw file with a **literal `<POINTER_SERVER>`** in it, and the "arrives pre-filled with your URL" behaviour silently dies | `API/Program.cs` |
| Placeholder token | `<POINTER_SERVER>` — in the `.Replace()` call **and** in the markdown bodies (`pointer-init.md:18,47,80`, `skill.md`) | `Program.cs` + `wwwroot/*.md` |
| JWT issuer/audience | `pointer-api` — see §4.6 | compose + `AuthenticationExtensions.cs` |
| Config section | `Pointer` — see §4.7 | `appsettings.json`, `Program.cs:180`, compose |
| `.pointer/config.json` key (added 2026-09-19) | `delegation` (`auto`\|`off`, absent = `auto`) — opt-out for the apply skill's cost-aware delegation (Step 3b: orchestrator hands mechanical edits to a cheaper worker model). **Key name is brand-neutral, not itself renamed**, but it is echoed as literal text `delegation=auto`/`delegation=off` in the `apply` prompt header next to `commitStyle=` — grep that format string when renaming anything nearby so the literal isn't mangled | `cli/src/config.ts` (`PointerConfig.delegation`), `cli/src/apply/context.ts`, `cli/src/apply/prompt.ts`, `docs/ON-DISK-CONTRACT.md` (`config.json` keys row), `Tests/OnDiskContractTests.cs` |
| Upload dir | `API/wwwroot/uploads/pointer-api` | per-project, keyed by project key — **data**, see §11 |

### 5.4 Skills (served + installed)

| Artifact | Today | Notes |
|---|---|---|
| `API/wwwroot/pointer-init.md` | `name: pointer-init` | Installed to `.claude/skills/pointer-init/SKILL.md` |
| `API/wwwroot/skill.md` | `name: pointer-feedback` | Installed to `.claude/skills/pointer-feedback/SKILL.md`. **Filename `skill.md` is a legacy URL** — new name should be `<kebab>-feedback.md` with `skill.md` kept as an alias if `LIVE_INSTALLS=yes`. **+R4-01 (2026-09-22):** gained a "Comment fields" section describing the new `PATCH api/comments/{id}/fields` endpoint — content only, served URL unchanged, no new brand strings introduced |
| `API/wwwroot/install.sh` | Writes both skills, `.agents/*` symlinks, `.pointer/pointer.sh`, `.pointer/bridge.mjs`, `.pointer/credentials.env{,.example}` | The single highest-leverage file: 20+ brand strings, and it defines the on-disk contract in customer repos |
| Skill **description trigger phrases** | "add Pointer to this app", "what are the pointer comments", "apply pending pointer comments" | These are how a user invokes the skill in natural language — rename them or the skill stops triggering on the new brand |
| `API/wwwroot/skills/apply.md` Step 3b (added 2026-09-19) | Reads `.pointer/config.json → delegation` and describes the cost-aware delegation flow; `skill.md` and `pointer-init.md` (Step 0 table) point readers at it | No brand strings introduced beyond the existing `.pointer/` path and prompt-header format already tracked above (§5.3) — still reword the surrounding prose consistently with the rest of the skill on rename |

### 5.5 Infrastructure

| File | Brand content |
|---|---|
| `Caddyfile` | 7 host blocks: `api.`, `app-angular.`, `app-react.`, `app-vue.`, `app.`, `demo.`, apex `pointer.moamen.work`; `root` paths `~/pointer-api/dashboard/*` |
| `docker-compose.prod.yml` | `POSTGRES_USER: pointer`, `POSTGRES_DB: pointer`, `ConnectionStrings__Default`, `Email__FromName: ${EMAIL_FROM_NAME:-Pointer}` |
| `.env.prod.example` | host + email defaults |
| `justfile` | `psql: docker compose exec db psql -U pointer -d pointer`, `bash -n API/wwwroot/pointer.sh`, widget build → `API/wwwroot/pointer.{js,css}`, `cd ../pointer-dashboard` |
| `DEPLOY.md` | VM paths `~/pointer-api`, `~/pointer-dashboard`, container names, all hostnames |
| VM (not in git) | `~/pointer-api`, `~/pointer-dashboard`, containers `pointer-api-{api,db,caddy}-1`, git remotes, DNS records, Brevo sender |

### 5.6 Code identifiers containing the brand

| Kind | Name | File |
|---|---|---|
| C# class | `PointerUrlResolver` | `API/Extensions/PointerUrlResolver.cs` |
| C# class | `ReassignPointerLandingOwnership` | migration — §4.1 |
| C# default | `"Pointer"` fallback From-name | `Infrastructure/Email/BrevoEmailSender.cs:34`, `API/Controllers/Admin/SettingsController.cs:66` |
| C# default | `BrandingService.DefaultProductName` | drives dashboard chrome, emails, install guide at runtime — **renaming this one constant renames most user-visible copy** |
| Angular | `PointerDogfoodService` + `core/pointer-dogfood/` dir + `WIDGET_TAG` | dashboard |
| TS const | `POINTER_*` config keys | widget, extension |

### 5.7 Generated clients (do not hand-edit)

`orval.config.ts` → three targets writing `clients/{angular,react,vue}/src` + `/model`, with
`customInstance` mutators. `clients/*/package.json` carry the package names. `scripts/generate-clients.mjs`
and `scripts/build-clients.mjs` orchestrate; `.github/workflows/publish-clients.yml` publishes and
auto-bumps. 479 generated files — phase 6 regenerates them; only the config, the two scripts, the
three `package.json` templates, the mutator files, and the workflow are edited by hand.

**+R4-01 (2026-09-22):** new Swagger/controller tag `Workspace` (`WorkspaceController`, §5.1) was
added to `orval.config.ts` `filters.tags` — without that entry the new `api/admin/workspace/*`
endpoints generate nothing, silently, same as any other ungated tag. Not yet regenerated against
production (deploy pending); the dashboard-agent picks this up on its next once-per-phase sync.

### 5.8 Tests with hardcoded hostnames

`Tests/{DemoUpgradeTests,MonetizationSignupTests,WorkspaceAdminOwnershipTests,ChangePasswordTests,PlanEnforcementTests,ApiKeyAuthTests,UserGovernanceTests}.cs` embed `*.pointer.moamen.work`.
They will keep passing after a careless rename (they assert on their own literals) — which makes them
a **silent** failure surface. Grep them explicitly.

### 5.9 Paths that must be renamed (not just their contents)

```
Pointer.sln
API/Pointer.API.csproj, API/Pointer.API.http
Application/Pointer.Application.csproj
Domain/Pointer.Domain.csproj
Infrastructure/Pointer.Infrastructure.csproj
Tests/Pointer.Tests.csproj
API/Extensions/PointerUrlResolver.cs
API/wwwroot/pointer.js, pointer.css, pointer.sh, pointer-init.md      (+ skill.md -> <kebab>-feedback.md)
API/wwwroot/uploads/pointer-api/                                       (data — §11)
.pointer/ , .pointer/credentials.env.example
extension/pointer-ext-v0.1.0.zip
extension/store-assets/pointer-{marquee-1400x560,small-tile-440x280,screenshot-1280x800}.jpg
landing/pointer-extension.zip
Infrastructure/Migrations/20260827124245_ReassignPointerLandingOwnership.{cs,Designer.cs}   (§4.1)
pointer-dashboard/angular/src/app/core/pointer-dogfood/
e2e/state/scratch/*/{.pointer,.claude/skills/pointer-*}                (regenerable fixtures)
```

### 5.10 Docs — decide the policy up front

38 files in `docs/` (specs, plans, monetization, branding SPEC) plus `AGENTS.md`, `CLAUDE.md`,
`DEPLOY.md`, `README.md`, the landing page, and `extension/store-assets/STORE_LISTING.md`.

**Recommended policy:** rewrite the *operational* docs (`AGENTS.md`, `CLAUDE.md`, `DEPLOY.md`,
`README.md`, `docs/AI_AGENT_TOKEN_OPTIMIZATION.md`, `docs/planning/branding/SPEC.md`, store listing);
for **historical** `docs/superpowers/{plans,specs}/*` and `docs/planning/*/PLAN-*.md`, either rename
them too (consistency, and the acceptance gate stays simple) or allowlist the whole directory with a
one-line note at the top of each: *"written pre-rebrand; 'Pointer' = <new name>"*. Pick one and
record it in §2 — do not leave it to the executing agent's judgment.

### 5.11 ⚠️ Brand residue that does NOT spell "pointer"

**The grep gate cannot see any of these.** They are brand-derived and must be handled by explicit
decision, not by search-and-replace. This is the category most likely to survive the whole project.

| Residue | Scale | Where | Public contract? |
|---|---|---|---|
| **`pf-` CSS class prefix** ("**p**ointer **f**eedback") | **175 distinct classes, 1,020 occurrences** — `pf-launcher`, `pf-toolbar`, `pf-sidebar`, `pf-pin`, `pf-modal`, `pf-toast`, `pf-btn`, `pf-card`, … | `web-component/src/**` (12 SCSS partials + the TS templates), the built `pointer.js`/`pointer.css`, `extension/src` | **Internally safe** — the widget renders in a shadow root (`element.ts:192 attachShadow({mode:'open'})`), so no page CSS can depend on them by cascade |
| **`--pf-*` custom properties** | the whole `$tokens` map in `styles/_variables.scss` (`--pf-primary`, `--pf-primary-hover`, `--pf-radius-lg`, …) | same | **YES — a documented public theming API.** `_variables.scss:3-17` tells consumers to write `pointer-feedback { --pf-primary: #0aa36e; }`, and custom properties **pierce the shadow DOM**. Renaming the prefix silently reverts every customer's theme to defaults (a `var()` fallback, no error) |
| **`ptr_` API-key prefix** | `ProfileService.cs:73` (`"ptr_" + …`), asserted in `Tests/ApiKeyAuthTests.cs:121,180`, and shown as a sample value in `install.sh`, `pointer-init.md:246`, `skill.md:167` | API + skills | **YES** — every key already issued starts with `ptr_` and must keep validating forever |
| **`moamen.work`** | 119 occurrences across both repos | Caddyfile, env files, 7 test files, extension manifest, docs, dashboards | The old domain; contains no "pointer" at all |
| **`admin@pointer.local`** | `.env.example:5` (`ADMIN__EMAIL`), `.env.prod.example:12` (`ADMIN_EMAIL`) | seeder input | Changing the value makes the seeder create a **second** admin rather than renaming the first — coordinate with §11.3 |
| **Artwork** | product logo, favicon, extension icons (16/32/48/128), 3 Chrome store images, landing imagery | DB (`Logo`/`Favicon` rows), `extension/icons/`, `extension/store-assets/` | Nothing greppable — see Q29 |
| **Binary filenames** | `extension/pointer-ext-v0.1.0.zip`, `landing/pointer-extension.zip`, `store-assets/pointer-*.jpg` | tracked binaries | A **content** grep never flags a filename; a filename scan is a separate check (§12.1) |

**Decisions required (Q26, Q27, Q29) — and the compat pattern if you rename them:**

```scss
// keeping customers' old overrides working while emitting the new token name
padding: var(--xy-radius-lg, var(--pf-radius-lg, 16px));   // COMPAT: remove <COMPAT_UNTIL>
```

```csharp
// new keys get the new prefix; every existing ptr_ key keeps validating
var prefix = "<new>_";                       // issuing
// validation must accept BOTH prefixes — never filter on the prefix
```

Add `ptr_`, `pf-`, and `moamen\.work` to the gate's `EXTRA_BRAND` pattern **once you have decided to
rename them**, so the gate proves the job is finished. If you decide to keep them, record that
decision here — an undocumented survivor is indistinguishable from an oversight six months later.
---

## 6. Execution order and prerequisites

### 6.1 Long-lead items — start these on day 1, in parallel with the code work

| Item | Why early | Blocks |
|---|---|---|
| Buy the domain (Q5) | — | everything below |
| Create DNS A records for all 7 hosts → the VM IP | TTL + propagation | §10.1, TLS issuance |
| Add + verify the Brevo sender domain (SPF, DKIM, DMARC) | Verification can take hours; unverified = **emails silently dropped** | §10.5, invite/approval flows |
| Create the four GitHub repos (Q11) | — | §8.1 |
| Confirm the npm package names are free | A taken name forces a rename mid-flight | §8.6 |
| Create the mailboxes (Q8/Q9) | — | §10.5 |
| Trademark screening for the new name | Not a code task, but the cheapest moment to discover a blocker | announcement |

### 6.2 Phase order (do not reorder — each depends on the previous)

| # | Phase | Section | Ends with gate |
|---|---|---|---|
| 0 | Interview + answers approved | §1–2 | Human "approved" on the §2 block |
| 1 | Prep: backup, branches, baseline counts | §8.0 | Baseline inventory file committed |
| 2 | Repositories created / mirrored / split | §8.1, §8.5 | All four remotes clone clean and build |
| 3 | API rename (namespaces, sln, csproj, code) | §8.2 | `dotnet build` + `dotnet test` green; EF probe clean (§4.2) |
| 4 | Widget rename (+ compat aliases) | §8.3, §9 | Widget builds; both tags register; picker works |
| 5 | Skills, installer, CLI | §8.4, §9 | `install.sh` end-to-end into a scratch repo |
| 6 | orval clients + publish | §8.6 | New packages installable from the registry |
| 7 | Dashboards ×3 (parity) | §8.7 | 3 builds green, Angular tests green, no forced logout |
| 8 | Extension | §8.8 | Loads unpacked; proxy + capture work |
| 9 | Docs, landing, agent guides | §8.9 | — |
| 10 | Infra: DNS, Caddy, compose, DB, email | §10 | All hosts 200 over TLS; test email delivered |
| 11 | Data migration | §11 | Branding row + project keys + screenshot URLs consistent |
| 12 | Verification | §12 | `verify-no-pointer.sh` exits 0; all 5 scenarios pass |
| 13 | Cutover + comms + deprecation clock | §13 | Old hosts redirect; deprecation date recorded |

### 6.3 Environment for the executing agent

```bash
# rebrand.env — created in phase 1 from the §2 answers; every phase sources it.
cd ~/rebrand && cat rebrand.env      # verify before each phase
set -a; . ~/rebrand/rebrand.env; set +a
```

Required tooling: `dotnet` 8 SDK + `dotnet-ef`, Node 22 (`node`, `npm`), `git` ≥ 2.40,
`git-filter-repo` (only if `HISTORY_MODE=filter-repo`), `gh` CLI authenticated with
`read:packages`+`write:packages`, `docker` (VM), `psql` (VM), `rg` or `grep -r`.

```bash
export PATH="$HOME/.dotnet/tools:/opt/homebrew/opt/node@26/bin:$PATH"
export NODE_AUTH_TOKEN=$(gh auth token)     # required by every per-app .npmrc
```

---

## 7. Replacement sets — token-aware, ordered, never a bare substring

Run these **in the given order** (longest/most-specific first), so a later rule cannot corrupt what
an earlier one produced. Every command excludes generated and vendored trees.

### 7.1 Standard exclusions (use in every command)

```bash
EXCL='--exclude-dir=node_modules --exclude-dir=.git --exclude-dir=obj --exclude-dir=bin
      --exclude-dir=dist --exclude-dir=.angular --exclude-dir=.next --exclude-dir=coverage
      --exclude-dir=clients --exclude-dir=TestResults --exclude-dir=.playwright-mcp
      --exclude-dir=.playwright-cli --exclude-dir=playwright-report --exclude-dir=test-results
      --exclude-dir=.zcode'
# clients/ is excluded because it is REGENERATED (§8.6), not edited.
# The browser-automation dirs hold DOM snapshots: 316 + 68 brand-matching files in pointer-api and
# 189 in pointer-dashboard. Left in, they bury every real hit — and editing them achieves nothing.
```

### 7.2 The protected list — these tokens must never be rewritten

```
pointer-events   pointerdown   pointerup   pointermove   pointercancel   pointerover
pointerout       pointerenter  pointerleave  gotpointercapture  lostpointercapture
PointerEvent     pointerId     pointerType   pointerPressure    setPointerCapture
releasePointerCapture   hasPointerCapture   cursor-pointer   "cursor: pointer"
any-pointer      pointer:coarse   pointer:fine   pointer-coarse   pointer-fine
```

Guard by asserting the count is unchanged before and after every phase:

```bash
protected_count() {
  grep -rEo 'pointer-events|pointer(down|up|move|cancel|over|out|enter|leave)|PointerEvent|pointerId|pointerType|setPointerCapture|releasePointerCapture|hasPointerCapture|cursor-pointer|cursor: *pointer|any-pointer|pointer:(coarse|fine)' . $EXCL 2>/dev/null | wc -l
}
# api repo baseline: 91   dashboard baseline: 56   (2026-09-08)
```

If the number drops, you renamed a DOM/CSS token. Revert that file.

### 7.3 Ordered replacement sets

Apply with word/context-anchored patterns. `SED='sed -i ""'` on macOS, `sed -i` on Linux.

| # | Match (regex) | Replace with | Scope |
|---|---|---|---|
| 1 | `poitner` | `${NAME_LOWER}` | everywhere (fix the typo **first**, or sets 4/6 will not see it). Known homes: `Application/Services/Implementation/BrandingService.cs:15` (`DefaultUrlDocs` — a **customer-facing** docs URL), all three `clients/*/package.json` `repository.url`, `justfile:17`, `.github/workflows/publish-clients.yml`, `DEPLOY.md`. Note `clients/react/package.json:28` reads `poitner-api`, so a replace keyed on the string `pointer-api` skips it entirely — that is the whole reason this set runs first |
| 2 | `Poitner` | `${NAME_PASCAL}` | everywhere |
| 3 | `pointer\.moamen\.work` | `${DOMAIN}` | everywhere — do **before** set 6, or `pointer` inside the host gets mangled |
| 4 | `@moamen-ui/pointer-(angular\|react\|vue)` | `${NPM_SCOPE}/${NAME_LOWER}-\1` | everywhere |
| 5 | `pointer-feedback-hl-style` → `pointer-feedback-hl` → `pointer-feedback` | `${NAME_KEBAB}-feedback…` | longest first |
| 6 | `pointer-(init\|dogfood\|api\|dashboard\|landing\|ext\|extension)` | `${NAME_KEBAB}-\1` | hyphenated compounds |
| 7 | `__POINTER_(CONFIG\|FETCH)__` | `__${NAME_UPPER}_\1__` | JS globals |
| 8 | `\b(VITE_\|REACT_APP_\|NEXT_PUBLIC_)?POINTER_([A-Z_]+)` | `\1${NAME_UPPER}_\2` | env vars |
| 9 | `\bpointer_([a-z_]+)` | `${NAME_SNAKE}_\1` | storage keys — **then apply §4.3's fallback**, do not stop here |
| 10 | `\bPointer\.(API\|Application\|Domain\|Infrastructure\|Tests)\b` | `${NAME_PASCAL}.\1` | C# namespaces + snapshot strings |
| 11 | `\bPointerUrlResolver\b`, `\bPointerDogfoodService\b`, `\bReassignPointerLandingOwnership\b` | `${NAME_PASCAL}…` | type names (§4.1 for the last one) |
| 12 | `pointer\.(js\|css\|sh)` | `${NAME_LOWER}.\1` | served assets |
| 13 | `\.pointer/` | `.${NAME_LOWER}/` | config dir (§4.5 if `LIVE_INSTALLS=yes`) |
| 14 | `-U pointer -d pointer`, `POSTGRES_(USER\|DB): pointer` | `${NAME_LOWER}` | DB identity (§10.4) |
| 15 | `\bPointer\b` (remaining, prose) | `${NAME_DISPLAY}` | docs, copy, email From-name, page titles |
| 16 | `\bpointer\b` (remaining, lowercase prose) | `${NAME_LOWER}` | **manual review each hit** — this is where DOM tokens hide |

Set 16 is the dangerous one. Run it as a **review list, not a bulk edit**:

```bash
grep -rn "\bpointer\b" . $EXCL | grep -vE "$(protected_regex)" > /tmp/set16-review.txt
wc -l /tmp/set16-review.txt     # walk every line by hand
```

### 7.4 File and directory renames

Use `git mv` so history follows. Full list in §5.9. Order: files first, then directories,
then update every reference (`.sln`, `.csproj` `ProjectReference`, `justfile`, workflows, `Caddyfile`,
`install.sh`, docs).

```bash
git mv Pointer.sln "${NAME_PASCAL}.sln"
for p in API/Pointer.API Application/Pointer.Application Domain/Pointer.Domain \
         Infrastructure/Pointer.Infrastructure Tests/Pointer.Tests; do
  git mv "$p.csproj" "$(dirname $p)/${NAME_PASCAL}.$(basename $p | sed 's/^Pointer\.//').csproj"
done
git mv API/Pointer.API.http "API/${NAME_PASCAL}.API.http"
git mv API/Extensions/PointerUrlResolver.cs "API/Extensions/${NAME_PASCAL}UrlResolver.cs"
# then fix Pointer.sln's project paths/GUID entries and every ProjectReference
grep -rn "Pointer\." *.sln */*.csproj      # must return nothing
```

---

## 8. Code rename, component by component

### 8.0 Phase 1 — prep (do not skip)

```bash
# 1. Full backups
pg_dump -U pointer -d pointer -Fc -f ~/backups/pre-rebrand-$(date +%F).dump      # on the VM
tar czf ~/backups/uploads-$(date +%F).tgz ~/pointer-api/API/wwwroot/uploads      # screenshots
cp -a ~/pointer-api/.env.prod ~/backups/env.prod-$(date +%F)                     # secrets

# 2. Clean tree + branch, in both repos
git status --porcelain    # MUST be empty
git switch -c rebrand/<new-name>

# 3. Baseline inventory (committed, so drift is provable)
{ echo "# baseline $(date -u +%FT%TZ)";
  for t in pointer Pointer POINTER poitner; do
    printf "%-8s %s files %s occurrences\n" "$t" \
      "$(grep -rl $t . $EXCL | wc -l)" "$(grep -ro $t . $EXCL | wc -l)"; done
  echo "protected tokens: $(protected_count)"; } > docs/rebranding/BASELINE.txt
# 4. Baseline the TEST SUITES too — never hardcode a remembered count.
#    (For reference: 309 [Fact]/[Theory] methods + 28 [InlineData] rows exist in Tests/ and 43
#    it() blocks in the Angular specs as of 2026-09-08, but the only number that matters is what
#    `dotnet test` / `npm test` actually report on YOUR checkout, right now, before any edit.)
dotnet test 2>&1 | tail -5 >> docs/rebranding/BASELINE.txt
(cd ../pointer-dashboard/angular && npm test -- --watch=false 2>&1 | tail -5) >> docs/rebranding/BASELINE.txt

git add docs/rebranding/BASELINE.txt && git commit -m "rebrand(1): baseline inventory"
```

**Gate 1:** `BASELINE.txt` committed in both repos; `protected_count` recorded (api 91 / dash 56).

### 8.1 Phase 2 — repositories

**Create the four repos** (Q11) empty, then per `HISTORY_MODE`:

```bash
# A) mirror (keeps all history) — for API and dashboard
git remote add new "$REPO_API" && git push new --mirror
git remote set-url origin "$REPO_API" && git remote remove new

# B) filter-repo split (keeps only that directory's history) — for widget and extension
git clone --no-local ~/Desktop/REPOS/pointer-api /tmp/split-widget
cd /tmp/split-widget && git filter-repo --path web-component/ --path-rename web-component/:
git remote add origin "$REPO_WIDGET" && git push -u origin main
# repeat with --path extension/ for REPO_EXTENSION

# C) fresh init — only if the human chose it in Q12; you lose blame forever
```

Then, in the **old** repos: update the README to point at the new URL and archive
(Settings → Archive) — do not delete (Q14). Anything still cloning them keeps working read-only.

**Also update every hardcoded repo reference:**

| Reference | File |
|---|---|
| `gh workflow run publish-clients.yml -R moamen-ui/poitner-api` | `justfile:` `publish-clients` |
| `repository.url: git+https://github.com/moamen-ui/poitner-api.git` | `clients/{angular,react,vue}/package.json` |
| GitHub Packages scope binding | `.github/workflows/publish-clients.yml` (`scope: '@moamen-ui'`) |
| Clone URLs in `DEPLOY.md`, `README.md`, `AGENTS.md`, `CLAUDE.md` (both repos) | docs |
| VM git remotes | `cd ~/pointer-api && git remote set-url origin …` (§10.6) |

> **GitHub Packages gotcha:** a scoped package published to `npm.pkg.github.com` is bound to the
> repository named in its `repository.url`. If that field still points at the old repo (or at a repo
> the workflow token cannot write to), `npm publish` fails with **403 / "Permission installation not
> allowed"**. Update all three `clients/*/package.json` in the same commit as the workflow.

**Gate 2:** each of the four new remotes clones into a clean directory and builds.

### 8.2 Phase 3 — API (.NET)

1. **Namespaces + types** — replacement sets 10, 11 (§7.3) across `API/ Application/ Domain/ Infrastructure/ Tests/`.
2. **File/project renames** — §7.4. Fix `.sln` project paths, all `ProjectReference` paths,
   `Directory.Build.props` if present, and `API/Properties/launchSettings.json`.
3. **Migrations** — §4.1 (the `[Migration]` string + the history-table SQL) and §4.2 (snapshot probe).
4. **Brand constants:**
   | Constant | File | New value |
   |---|---|---|
   | `BrandingService.DefaultProductName` | `Application/Services/Implementation/BrandingService.cs` | `${NAME_DISPLAY}` |
   | From-name fallback `"Pointer"` | `Infrastructure/Email/BrevoEmailSender.cs:34` | `${NAME_DISPLAY}` |
   | From-name default `?? "Pointer"` | `API/Controllers/Admin/SettingsController.cs:66` | `${NAME_DISPLAY}` |
   | `Email__FromName: ${EMAIL_FROM_NAME:-Pointer}` | `docker-compose.prod.yml:46` | `${NAME_DISPLAY}` |
   | `POINTER_SERVER` env read | `API/Program.cs:193` | `${NAME_UPPER}_SERVER` |
5. **The surfaces that fail silently** — do these explicitly, they are easy to miss:
   | Surface | File | Failure if missed |
   |---|---|---|
   | CORS dashboard allow-list (`string[] dashboardOrigins`) | `API/Program.cs:61-68` | Renamed dashboards are CORS-blocked on every `/api/admin/*` call. Add new origins **and keep the old ones** until cutover completes |
   | Static-file cache special-case by **filename** | `API/Program.cs:250-252` | Renamed widget assets lose `Cache-Control: no-cache`; shipped fixes stop reaching customer pages |
   | `injectedFiles` HashSet + `<POINTER_SERVER>` placeholder | `API/Program.cs:197-201, 212` | Served skills arrive with a literal `<POINTER_SERVER>` (§8.4) |
   | `/embed.js` generated inline | `API/Program.cs:283-319` | Emits the old tag / old `pointer.js` path / `window.__pointerEmbedded` |
   | `Pointer` config section (3 places) | §4.7 | Swagger widget embed silently disables itself |
   | `JWT__Issuer` | §4.6 | **Mass 401** |
   | `ptr_` API-key prefix | `Application/Services/Implementation/ProfileService.cs:66-73` + `Tests/ApiKeyAuthTests.cs:121,180` | Per Q27. If changed, validation must accept both prefixes |
   | `DefaultUrlDocs` with the `poitner` typo | `Application/Services/Implementation/BrandingService.cs:15` | A customer-facing docs link pointing at a dead repo |
   | `.env.example` **and** `.env.prod.example` | root | `Database=pointer`, `JWT__Issuer=pointer-api`, `ADMIN__EMAIL=admin@pointer.local`, `POINTER_SERVER`, `EMAIL_FROM_NAME` — new contributors get a half-renamed dev setup |
6. **Tests with hardcoded hosts** (§5.8) — replacement set 3 covers them, but assert explicitly:
   `grep -rn "moamen.work" Tests/` must return nothing (unless the human keeps the old domain).
7. **`Pointer.API.http`** — rename and rewrite its `@host` variable.
8. **`justfile`** — `psql` DB/user, `test-cli` path, widget build comment and output paths,
   `publish-clients` repo flag, the `dev` recipe's `cd ../pointer-dashboard`.
9. **`API/wwwroot/admin/app.js`** — static admin app storage keys (§4.3 pattern applies here too).

```bash
dotnet build && dotnet test          # compare against docs/rebranding/BASELINE.txt
dotnet csharpier .                   # keep formatting canonical
```

**Gate 3:** build green and the test count/pass-list matches the phase-1 baseline; EF probe (§4.2) produces an empty migration; `grep -rin "pointer" API Application Domain Infrastructure Tests $EXCL` returns only §12 allowlist entries.

### 8.3 Phase 4 — widget (`web-component/` → the highest-risk contract)

| Change | File |
|---|---|
| `customElements.define('pointer-feedback', …)` → `${NAME_KEBAB}-feedback` | `src/index.ts:8` |
| Highlight class `pointer-feedback-hl` | `src/constants.ts:3` |
| Style element id `pointer-feedback-hl-style` | `src/dom.ts:40` |
| Tag reference in the capture payload (it records the widget's own tag to exclude it from selectors) | `src/capture.ts:73` |
| `__POINTER_CONFIG__`, `__POINTER_FETCH__` | src + built bundle |
| Storage keys `pointer_token`, `pointer_user`, `pointer_visible`, `pointer_toolbar_pos`, `pointer_page_session_id`, `pointer_env_*` | src — **with §4.3 fallback** |
| `VITE_POINTER_*`, `REACT_APP_POINTER_*`, `NEXT_PUBLIC_POINTER_SERVER`, `POINTER_*` env sniffing | src — **keep reading the old keys too** (§9), host apps carry them |
| Build output paths `API/wwwroot/pointer.{js,css}` → `${NAME_LOWER}.{js,css}` | `web-component/*` build config + `justfile` |

**Scope check — 22 of the 23 files under `web-component/src/` carry the brand,** not the 4 named
above. The full set:

```
auth-ui.ts  capture.ts  constants.ts  dom.ts  element.ts  framework-source.ts  icons.ts
index.ts    pagecontext.ts  shortcut.ts  templates.ts  types.ts
styles/index.scss  styles/_variables.scss  styles/_launcher.scss  styles/_toolbar.scss
styles/_sidebar.scss  styles/_card.scss  styles/_modal.scss  styles/_popover.scss
styles/_pins.scss  styles/_toast.scss  styles/_tooltip.scss
```

**The 12 SCSS partials are mostly *protected* tokens, not brand** — their `pointer` hits are
`pointer-events` (9×) and `cursor: pointer`. The brand in them is the **`pf-` prefix** (§5.11) and
the host-tag selector `pointer-feedback` in `_variables.scss:3,10` and `index.scss:1,7`.

**`pf-` decision (Q26).** 175 classes / 1,020 occurrences. The classes themselves are inside a
shadow root (`element.ts:192`), so renaming them is internally safe. The **`--pf-*` custom properties
are a public API** — `_variables.scss:3-17` documents `pointer-feedback { --pf-primary: … }` and
custom properties pierce the shadow boundary. If you rename the prefix, emit both:

```scss
// $tokens compiles each token to var(--pf-<name>, <default>); keep the old name as the fallback
color: var(--<new>-primary, var(--pf-primary, #2563eb));   // COMPAT: remove <COMPAT_UNTIL>
```

Also note `element.ts:199` builds the stylesheet URL as `${this.server}/pointer.css` with an
`injected?.cssUrl || CSS_URL` override chain — all three paths need the new filename.

**Do not hand-edit `API/wwwroot/pointer.js`** — it is the built bundle. Edit `web-component/src/*`
and rebuild (`just widget-build`, or `npm run build` in the widget repo), then commit the artifact.

**`pointer-events` warning:** the overlay and highlight styles rely on `pointer-events: none`.
Verify `protected_count` in this directory is unchanged after editing.

**Gate 4:** `npm run build` in the widget; the built bundle registers the new tag; a scratch HTML page
with the new tag can pick an element and post a comment; `pointer-events` count unchanged.

### 8.4 Phase 5 — skills, installer, CLI

| Artifact | Rename | Notes |
|---|---|---|
| `API/wwwroot/pointer-init.md` | `${NAME_KEBAB}-init.md` | Update `name:` frontmatter **and** the `description:` trigger phrases ("add ${NAME_DISPLAY} to this app", …) — the description is what makes the skill fire |
| `API/wwwroot/skill.md` | `${NAME_KEBAB}-feedback.md` | Same. Keep `skill.md` as a served alias if `LIVE_INSTALLS=yes` |
| `API/wwwroot/install.sh` | — | ~20 brand strings: skill URLs, install paths `.claude/skills/${NAME_KEBAB}-{init,feedback}/SKILL.md`, `.agents/*` symlinks, `.${NAME_LOWER}/${NAME_LOWER}.sh`, `bridge.mjs`, `credentials.env{,.example}` scaffolding, all echo lines |
| `API/wwwroot/pointer.sh` | `${NAME_LOWER}.sh` | Env resolution order reads `${NAME_UPPER}_SERVER`/`_PROJECT`/`_API_KEY` from `.env*` then `.${NAME_LOWER}/credentials.env` — **keep the `POINTER_*` names as a fallback** (§9) |
| `API/wwwroot/bridge.mjs` | — | brand strings + the local Apply endpoint |
| `API/wwwroot/embed.js` | — | emits the snippet; must emit the new tag |
| `.pointer/credentials.env.example` (both repos) | `.${NAME_LOWER}/…` | Keys `${NAME_UPPER}_{SERVER,PROJECT,EMAIL,PASSWORD,API_KEY}` |
| `.gitignore` (both repos) | `.${NAME_LOWER}/` | Keep the old `.pointer/` ignore line too, so a contributor's stale dir is never committed |
| `e2e/` fixtures | regenerate | They contain `.pointer/` + `.claude/skills/pointer-*` scratch dirs |

> **Rename these four things in one commit or the installer breaks silently:**
> the wwwroot **filenames**, the `injectedFiles` HashSet in `API/Program.cs:197-201`, the
> `<POINTER_SERVER>` placeholder inside the markdown bodies, and the `.Replace("<POINTER_SERVER>", …)`
> call at `Program.cs:212`. Verify with
> `curl -s $HOST_API/<new>-init.md | grep -c '<.*_SERVER>'` → must be **0**, and the real host URL
> must appear in its place. A stale HashSet produces a 200 with a broken body — no error anywhere.

The **installed** skill's own instructions must also be rebranded — they tell the agent which files
to read (`.${NAME_LOWER}/credentials.env`), which endpoints to call, and which phrases to answer to.

**Gate 5:** `curl -fsSL $HOST_API/install.sh | sh` in an empty scratch repo produces the new paths,
both skills resolve, and `./.${NAME_LOWER}/${NAME_LOWER}.sh list` returns comments.

### 8.5 The build boundary created by the repo split (answers Q13)

Today one repo builds everything: `web-component/` compiles **into** `API/wwwroot/pointer.js`, and
`justfile` drives both. After the split that path crosses a repo boundary. Choose one, and record it:

| Option | How | Trade-off |
|---|---|---|
| **A. Publish the widget as an npm package** and have the API fetch it at build/deploy time | widget repo publishes `${NPM_SCOPE}/${NAME_LOWER}-widget`; API's Docker build/CI downloads and copies it into `wwwroot/` | Cleanest; adds a release step and a version to bump before every deploy |
| **B. Commit the built bundle into the API repo** (status quo, manual) | widget repo builds, you copy `${NAME_LOWER}.{js,css}` into the API repo and commit | Simple; the artifact can drift from its source, silently |
| **C. Git submodule / subtree** | API repo carries the widget repo at `web-component/` | Keeps one build; submodules are a recurring papercut for every clone and for CI |
| **D. Don't split the widget** | keep `web-component/` in the API repo | Least work; contradicts Q11's four-repo plan |

**Recommendation: B for the cutover, A immediately after** — B keeps phase 4's gate simple, A is the
right steady state. Same question applies to `extension/` (it only shares the store assets and README,
so splitting it is cheap) and to `landing/` (decide whether it goes with the API repo or its own).

### 8.6 Phase 6 — generated clients, orval, npm

1. `clients/{angular,react,vue}/package.json`: `name` → `${PKG_*}`, `description`,
   `repository.url` → the **new API repo** (see the GitHub Packages gotcha in §8.1).
2. Root `package.json`: `"name": "pointer-api-clients"` → `${NAME_LOWER}-api-clients`.
3. `orval.config.ts`: no brand in the targets today, but re-read it — the mutator paths and
   `filters.tags` must still resolve after any file moves.
4. `scripts/generate-clients.mjs:24`: reads **`process.env.POINTER_SWAGGER_URL`** first, then falls
   back to `http://localhost:8090/swagger/v1/swagger.json`. The workflow sets that env var to a
   **hardcoded old host** (`.github/workflows/publish-clients.yml:54`:
   `POINTER_SWAGGER_URL: https://api.pointer.moamen.work/swagger/v1/swagger.json`). Rename the
   variable **and** repoint the URL — in both places, or generation silently keeps reading the old
   deployment's spec. The workflow generates from the **live API**,
   so **the API must be deployed at the new host before the first client publish** (or run generation
   locally against `localhost`).
5. `scripts/build-clients.mjs`: check for package-name assumptions.
6. `.github/workflows/publish-clients.yml`:
   - `scope:` → `${NPM_SCOPE}`
   - the auto-bump step runs `npm view @moamen-ui/pointer-angular version || echo 0.0.0`.
     **With a brand-new package name this returns nothing → `CUR=0.0.0` → the first publish becomes
     `0.0.1`, not `1.0.0`.** Pass the explicit `version` input on the first run
     (`gh workflow run publish-clients.yml -f version=${PKG_START_VER}`), then let auto-bump resume.
   - update the `npm view` package name and the summary line.
7. Regenerate and publish:
   ```bash
   npm ci && npm run generate-clients && npm run build-clients
   gh workflow run publish-clients.yml -R <new-api-repo> -f version="${PKG_START_VER}"
   npm view "${PKG_ANGULAR}" version    # confirm
   ```
8. `npm deprecate '@moamen-ui/pointer-angular@*' 'Renamed to ${PKG_ANGULAR}'` (×3). **Never unpublish.**

**Gate 6:** all three new packages resolve from the registry with `NODE_AUTH_TOKEN` set, and
`clients/` contains no brand string except the intended package names.

### 8.7 Phase 7 — the three dashboards (parity rule applies: identical change in all three)

Per `CLAUDE.md`, every change lands in `angular/`, `react/`, and `vue/`. Dispatch one agent per app
with the same brief, then diff the three results for parity.

| Change | Angular | React | Vue |
|---|---|---|---|
| Dependency + 74 import sites | `@moamen-ui/pointer-angular` → `${PKG_ANGULAR}` | idem | idem |
| Storage keys **with fallback** (§4.3) | `core/prefs/preferences.service.ts`, `features/shell/demo-panel.component.ts`, `shared/install-guide/install-guide.service.ts` | `src/lib/storage.ts` | `src/lib/storage.ts`, `src/lib/demoSession.ts` |
| API base URL | `src/environments/environment*.ts` | `.env.development`, `.env.production` (`VITE_API_BASE`) | same |
| Page title | `src/index.html` | `index.html` (`<title>Pointer Admin</title>`) | `index.html` |
| Dogfood widget | rename `core/pointer-dogfood/` dir + `PointerDogfoodService` + `WIDGET_TAG` + `${apiBase}/pointer.js` | n/a (Angular-only today — note it) | n/a |
| Install-guide copy + snippets | `shared/install-guide/install-guide.component.ts` (emits `<pointer-feedback …>`, `/pointer.js`, `install.sh`, the init prompt) + its spec | mirror | mirror |
| i18n — **132 literals, measured** | `public/assets/i18n/en.json` (23) + `ar.json` (23) | `en.json` (21) + `ar.json` (21) | `en.json` (22) + `ar.json` (22) |
| Hardcoded landing URL `EXTENSION_ZIP_URL = 'https://pointer.moamen.work/pointer-extension.zip'` | `shared/install-guide/install-guide.component.ts:40` (+ asserted in `install-guide.spec.ts:96,208`) | `src/lib/install-guide.ts:10` | `src/shared/install-guide/buildSteps.ts:11` |
| Branding fallback `productName: 'Pointer'` | `core/branding/*` | `src/lib/branding*` | `src/composables/useBranding.ts:37`, `features/branding/BrandingPage.vue:100,309` |
| Export filename `pointer-comments-<key>.json` | projects page | projects page | `features/projects/ProjectsPage.vue:255` |
| Favicon / logo / manifest assets | `public/` | `public/` | `public/` |
| Docs | `AGENTS.md`, `CLAUDE.md`, `README.md`, `.gitignore`, `.pointer/` | — | — |

> **If you need to verify a dashboard before the packages are published**, do not reorder the
> phases — point the dependency at the local build instead:
> `npm i file:../../<api-repo>/clients/angular` (or `npm link`), build, then restore the registry
> version before committing. Committing a `file:` dependency is how a lockfile ends up unbuildable
> in CI.

```bash
for a in angular react vue; do (cd $a && npm i "$PKG" && npm run build) || echo "FAIL $a"; done
(cd angular && npm test -- --watch=false)     # compare against the baseline
```

**On the i18n files:** the copy is *not* all runtime-branded. 132 hardcoded literals live in the six
`{en,ar}.json` files — install-guide hints naming `pointer-init`/`pointer-feedback`/`.pointer/…`,
settings hints ("the maximum number of emails Pointer sends per day"), `"brand": "Pointer Admin"`,
`"productNamePlaceholder": "e.g. Pointer"`, and the extension steps. The **Arabic** files carry the
Latin-script brand inline inside RTL sentences (`أداة Pointer`, `خادم Pointer`) — per Q4b, either keep
that convention or apply one agreed transliteration consistently. Do **not** let three agents each
invent their own Arabic rendering: decide once, put it in the answers block, and diff the three
`ar.json` results for identical wording.

Note also: several literals name the **skills** and the **`.pointer/` path** in user-facing help text.
Those must match what `install.sh` actually writes (§8.4), or the dashboard tells users to run
commands that produce different paths than the guide shows.

**Gate 7:** three green builds, Angular tests green at baseline, and the **no-forced-logout check**: with a
session created *before* the change, reload → still signed in, language and theme preserved.

### 8.8 Phase 8 — browser extension

| Item | File | Note |
|---|---|---|
| `"name": "Pointer Feedback"` | `manifest.json` | Store name ≤45 chars |
| `"description"` | `manifest.json` | ≤132 chars — use `TAGLINE` |
| `"default_title"` | `manifest.json` | — |
| `host_permissions` / DNR rules | `manifest.json` | Update `*.pointer.moamen.work` → `*.${DOMAIN}` — **stale host permissions silently break the proxy** |
| `pointer_via_proxy__` storage flag | `src/shared.ts:17` | §4.3 pattern |
| **postMessage protocol discriminators** — atomic, and *not* atomic with the server | `inject-main.ts:20` `PROXY_TOKEN = '__pointer_via_proxy__'`; `inject-main.ts:52,64` send `source: 'pointer-ext'`; `inject-main.ts:29` expects `source: 'pointer-ext-res'`; `content-bridge.ts:22` filters on `'pointer-ext'`; `background.ts:269` on `'pointer-ext'`, `background.ts:180` logs `[pointer-ext]` | Rename all five together or the proxy path dies silently (messages are simply ignored — no error). **And note Chrome Web Store updates roll out over days**, so an old extension will be talking to a new server for a while: if you rename these, the *server-side* and *page-side* halves must accept both discriminators until the store rollout completes |
| `DEFAULT_SERVER = 'https://api.pointer.moamen.work'` | `src/shared.ts` (used by `options.ts:12,24`, `background.ts`) | The extension's default server; stale value points users at the old host |
| **5 of 6 `src/` files carry the brand** | `background.ts`, `content-bridge.ts`, `inject-main.ts`, `popup.ts`, `shared.ts` (only `options.ts` is clean) | `inject-main.ts` is where the injected `pointer.js` loader and the `<pointer-feedback>` tag literal live — the extension injects the same widget contract as a customer page, so §9's aliases apply to it too |
| Popup/options copy | `src/popup.ts`, `popup.html`, `options.html` | — |
| Icons | `icons/{16,32,48,128}.png` | New logo |
| Store assets | `store-assets/pointer-{marquee,small-tile,screenshot}.*` + `STORE_LISTING.md` | Rename files + regenerate imagery |
| Packaged zips | `extension/pointer-ext-v0.1.0.zip`, `landing/pointer-extension.zip` | Rebuild, rename, and update the landing page's download link |

If `EXT_PUBLISHED=yes`: the extension **ID stays the same** (it is tied to the store listing / key).
A name change is a listing update and goes through review — schedule it, and keep the old name in the
listing's "formerly" line for one release so existing users recognise it.

**Gate 8:** loads unpacked without manifest errors; the popup connects to the new API host; capture
and proxy paths work on a test page.

### 8.9 Phase 9 — docs, landing, agent guides

- `AGENTS.md` + `CLAUDE.md` in **both** repos: product name, repo URLs, package names, the
  "never call the API with raw axios" rule (package names appear inside it), the parity table.
- `README.md` (both), `DEPLOY.md`, and the root-level docs corpus — measured: `docs/SELF_HOSTING.md`,
  `docs/DESIGN.md`, `docs/PLAN.md`, `docs/TASKS.md`, `docs/E2E_TEST_PLAN.md`,
  `docs/ADMIN_WEB_DESIGN.md`, `docs/ADMIN_PREFS_I18N_DESIGN.md`,
  `docs/AI_AGENT_TOKEN_OPTIMIZATION.md`, `docs/planning/branding/SPEC.md`,
  `extension/store-assets/STORE_LISTING.md` (38 files under `docs/` in total).
- `landing/index.html`: all copy, the extension download link, hostnames, install snippet.
- Historical `docs/` — apply the §5.10 policy chosen in the interview.
- Add a `docs/rebranding/CHANGELOG-rebrand.md` recording old → new for every identifier, so a future
  reader can decode old commits, old customer support threads, and old log lines.

---

## 9. Compatibility layer — only if `LIVE_INSTALLS=yes`

**Principle: nothing in a customer's app or repo may break on deploy.** The rename *adds* new names
and *deprecates* old ones; it never swaps them out from under a live install. If `LIVE_INSTALLS=no`,
delete this section's work from the plan and rename hard — it is roughly 40% of the effort.

| Old contract | Compatibility measure | Remove on |
|---|---|---|
| `<pointer-feedback>` in customer HTML | Register **both** tags to the same class: `customElements.define('${NAME_KEBAB}-feedback', X); customElements.define('pointer-feedback', class extends X {})` — a class can only be registered once, so the alias must be a trivial subclass. Log one `console.info` deprecation notice | `COMPAT_UNTIL` |
| `GET /pointer.js`, `/pointer.css` | Serve both paths (copy the artifact, or a Caddy `handle /pointer.js { rewrite * /${NAME_LOWER}.js }`) | `COMPAT_UNTIL` |
| `GET /skill.md`, `/pointer-init.md`, `/pointer.sh`, `/install.sh` | Keep the old URLs serving the **new** content — already-installed skills fetch by URL | `COMPAT_UNTIL` |
| `.pointer/credentials.env`, `.pointer/pointer.sh` in customer repos | The new CLI and skills read `.${NAME_LOWER}/` **first**, then fall back to `.pointer/`. `install.sh` may additionally leave a one-line `.pointer/MOVED` note | `COMPAT_UNTIL` |
| `.claude/skills/pointer-{init,feedback}/` installed in customer repos | `install.sh` writes the new dirs and leaves the old ones in place (a stale duplicate skill is harmless; a broken one is not). Optionally print "you can delete `.claude/skills/pointer-*`" | `COMPAT_UNTIL` |
| `VITE_POINTER_*`, `REACT_APP_POINTER_*`, `NEXT_PUBLIC_POINTER_SERVER`, `POINTER_*` | Widget + CLI read `${NAME_UPPER}_*` first, then the `POINTER_*` name | `COMPAT_UNTIL` |
| `__POINTER_CONFIG__` / `__POINTER_FETCH__` window hooks | Read the new global, fall back to the old | `COMPAT_UNTIL` |
| Browser storage keys | §4.3 migrate-on-read — **mandatory regardless of `LIVE_INSTALLS`**, because your own dashboard users have live sessions | one release after cutover |
| Old hostnames | Keep `*.pointer.moamen.work` in the Caddyfile, `redir` to the new host (301 for pages, but **proxy — not redirect — `api.`**: a 301 breaks non-following clients and CORS preflights) | `COMPAT_UNTIL` |
| **`--pf-*` theming tokens** in customer stylesheets | Emit `var(--<new>-x, var(--pf-x, <default>))` so old overrides still win (§8.3). Custom properties pierce the shadow DOM, so this **is** a public API | `COMPAT_UNTIL` |
| **`ptr_` API keys** already issued | Keep validating them forever — never filter on the prefix. Only newly issued keys carry the new prefix | never |
| **Old served paths** `/pointer-init.md`, `/skill.md`, `/install.sh` | Keep them in `Program.cs`'s `injectedFiles` set **alongside** the new names, so already-installed skills that re-fetch keep getting a configured file | `COMPAT_UNTIL` |
| **`<POINTER_SERVER>` placeholder** | Have `Program.cs` replace **both** the old and new placeholder tokens; customer-side skill copies may contain either | `COMPAT_UNTIL` |
| **Old CORS origins** | Keep the five `*.pointer.moamen.work` entries in `dashboardOrigins` until the old hosts are retired | `COMPAT_UNTIL` |
| **`JWT__Issuer`** | Freeze, or dual-accept — §4.6. **Not** a same-deploy swap | see §4.6 |
| Old npm packages | `npm deprecate`, never unpublish | never |

Record every alias in `docs/rebranding/CHANGELOG-rebrand.md` with its removal date, and create one
calendar reminder for `COMPAT_UNTIL`. Aliases with no owner and no date become permanent.

---

## 10. Infrastructure, DNS, email, database

### 10.1 DNS

Create A records → the VM IP for: `api`, `app`, `app-angular`, `app-react`, `app-vue`, `demo`, and
the apex. Keep the old records until `COMPAT_UNTIL`. Caddy issues TLS on first request per host —
a missing record shows up as a cert failure, not a 404.

### 10.2 Caddyfile

Seven host blocks today (`Caddyfile:22,34,39,44,50,57,63`). Add the new hosts, keep the old ones with
redirects (§9). Update the `root * /srv/dashboard/*` paths only if the VM directory layout changes.

```bash
docker compose exec caddy caddy validate --config /etc/caddy/Caddyfile
docker compose restart caddy
for h in api app app-angular app-react app-vue demo; do
  echo -n "$h: "; curl -s -o /dev/null -w "%{http_code}\n" "https://$h.$DOMAIN/"; done
curl -s -o /dev/null -w "landing: %{http_code}\n" "https://$DOMAIN/"
```

### 10.3 docker compose — ⚠️ the named-volume trap (read before renaming any directory)

`docker-compose.prod.yml` declares `pgdata` and `uploads` as named volumes with **no explicit
`name:`** and no `COMPOSE_PROJECT_NAME`. Docker derives the project name from the **directory name**,
so today the real volumes are `pointer-api_pgdata` and `pointer-api_uploads`.

**Renaming `~/pointer-api` → `~/${NAME_LOWER}-api` on the VM creates a NEW, EMPTY pair of volumes.**
The stack comes up looking like a fresh install: no users, no comments, no screenshots. The old data
is still there, orphaned, but the app will have already run migrations against an empty DB.

Pick one, deliberately:

| Option | Commands | Notes |
|---|---|---|
| **A. Pin the project name (safest)** | Add `name: pointer-api` at the top of the compose file, or `COMPOSE_PROJECT_NAME=pointer-api` in `.env.prod` | Keeps the existing volumes; leaves one "pointer" string in infra config — allowlist it, or accept option B |
| **B. Migrate the volumes** | `docker compose down`; `docker volume create ${NAME_LOWER}-api_pgdata`; copy: `docker run --rm -v pointer-api_pgdata:/from -v ${NAME_LOWER}-api_pgdata:/to alpine sh -c 'cd /from && cp -a . /to'`; repeat for `uploads`; then rename the directory and `docker compose up -d` | Full rename, requires downtime + a verified backup first |
| **C. Dump/restore** | `pg_dump` from the old stack, `psql` into the new one; `tar` the uploads volume | Slowest, most auditable |

Also update: container names in `DEPLOY.md` (`pointer-api-{api,db,caddy}-1` follow the project name),
and any systemd unit or cron referencing the old path.

### 10.4 Postgres database and role (Q22)

Today `POSTGRES_USER: pointer`, `POSTGRES_DB: pointer` (`docker-compose.prod.yml:11,13`) and
`justfile: psql` uses `-U pointer -d pointer`. These are internal and never seen by users, so
**renaming them is optional**. If you do:

```sql
-- connect to the 'postgres' database; you cannot rename the DB you are connected to
ALTER DATABASE pointer RENAME TO <newname>;
ALTER ROLE     pointer RENAME TO <newname>;
-- Re-set the password after renaming the role: with MD5-hashed passwords the hash is
-- salted with the role name, so a rename invalidates it (SCRAM is unaffected, but be explicit).
ALTER ROLE <newname> WITH PASSWORD '<DB_PASSWORD>';
```

Then update `ConnectionStrings__Default`, both `POSTGRES_*` vars, and `justfile`. Requires the stack
stopped (no open connections) and a verified backup. **If you are not doing option 10.3B/C in the
same window, skip this** — a half-renamed DB identity is worse than a consistent old one.

### 10.5 Email (Brevo)

- Verify the new sending domain (SPF + DKIM + DMARC) **before** cutover — an unverified sender does
  not error loudly, it just stops delivering invites, approvals, and password resets.
- Set `EMAIL_FROM_EMAIL` and `EMAIL_FROM_NAME` in `.env.prod`; they override the code defaults
  (`Email:FromEmail` / `Email:FromName`) via `docker-compose.prod.yml:45,46`.
- The **admin Settings page** stores From-name/From-email in the DB (`AppSetting` rows) and those win
  over env defaults — update them in the UI, or in SQL (§11), or the old brand keeps sending mail.
- Re-check email templates for the product name (most come from `BrandingService`, but verify the
  subject lines and the approve/reject flows in `UserService.cs:192,220` and `DemoService.cs:181`).
- Send one real invite to yourself and read the raw headers before declaring this done.

### 10.6 VM (not in git)

```bash
ssh ubuntu@<vm>
cd ~/pointer-api  && git remote set-url origin "$REPO_API"
cd ~/pointer-dashboard && git remote set-url origin "$REPO_DASHBOARD"
# directory renames: only together with §10.3's volume decision
```
Update `DEPLOY.md` to match reality afterwards, including the dashboard build steps
(`node:22` container + `NODE_AUTH_TOKEN`) which reference the package names.

---

## 11. Data migration — the brand inside the database and on disk

Code changes do not touch these. Run them in the cutover window, from a backup.

### 11.1 Database object names — audited: the schema carries NO brand

Verified 2026-09-08 against the migrations and the EF model snapshot.

**18 physical tables**, all `snake_case`, none containing the brand:

```
ai_rules            app_environments   app_settings                   comments
extension_sites     invites            page_context_snapshots         plans
predefined_actions  predefined_action_suggestions   project_app_urls  projects
replies             role_tenant_overrides           roles             status_presentations
subscriptions       users
```

Plus EF's own `__EFMigrationsHistory`. (The C# `DbSet` properties are PascalCase — `AppSettings`,
`Comments`, `Users` — but the **physical** names are snake_case. Use the physical names in every SQL
statement in this plan; quoting a PascalCase name will fail with `relation does not exist`.)

**Columns** are `snake_case` too (`id`, `owner_id`, `project_id`, `body`, `element`, `api_key`,
`approval_status`, `is_super_admin`, `is_bug_report`, `picked_actions`, `created_at`, `deleted_by`, …).
Constraints and indexes follow EF's generated convention (`PK_comments`,
`FK_comments_projects_project_id`, `IX_ai_rules_owner_id_project_id`).

**Audit result — zero brand occurrences in:** table names, column names, index names, constraint
names, sequence names, enum types, schemas (the app uses `public`; no `HasDefaultSchema`).

> **Therefore: do not rename any table, column, index, or constraint.** There is nothing to gain —
> no user, customer, or API consumer ever sees these identifiers — and everything to lose: each
> rename is an EF migration against production data, and EF renames columns via
> `ALTER TABLE … RENAME COLUMN`, which will break any hand-written SQL, view, or backup script that
> references the old name. **Renaming schema objects is out of scope for this rebrand.**

Confirm the audit still holds before the cutover (should print no rows):

```sql
-- tables, columns, constraints, indexes, sequences, enum labels
SELECT 'table'  AS kind, table_name  AS name FROM information_schema.tables
  WHERE table_schema='public' AND table_name ILIKE '%pointer%'
UNION ALL SELECT 'column', table_name||'.'||column_name FROM information_schema.columns
  WHERE table_schema='public' AND column_name ILIKE '%pointer%'
UNION ALL SELECT 'constraint', constraint_name FROM information_schema.table_constraints
  WHERE constraint_schema='public' AND constraint_name ILIKE '%pointer%'
UNION ALL SELECT 'index', indexname FROM pg_indexes
  WHERE schemaname='public' AND indexname ILIKE '%pointer%'
UNION ALL SELECT 'sequence', sequence_name FROM information_schema.sequences
  WHERE sequence_schema='public' AND sequence_name ILIKE '%pointer%'
UNION ALL SELECT 'enum label', e.enumlabel FROM pg_enum e WHERE e.enumlabel ILIKE '%pointer%';
```

### 11.2 What the brand DOES touch at the database layer

| Layer | Object | Action | Section |
|---|---|---|---|
| Cluster | database name `pointer` | optional rename | §10.4 |
| Cluster | role/user `pointer` | optional rename (re-set the password after) | §10.4 |
| Connection | `ConnectionStrings__Default`, `POSTGRES_USER`, `POSTGRES_DB`, `justfile: psql` | must match whatever §10.4 decides | §10.3–10.4 |
| Docker | volume `pointer-api_pgdata` holding the cluster | **do not orphan it** | §10.3 |
| Schema | `__EFMigrationsHistory` row `20260827124245_ReassignPointerLandingOwnership` | frozen, or the documented `UPDATE` | §4.1 |
| **Row values** | `AppSettings.value` (product name, From-name/From-email, branding URLs) | `UPDATE` | §11.3 |
| **Row values** | `Projects.key` = `pointer-api`, `pointer-landing` | optional (Q23), with the upload dir + stored URLs | §11.3 |
| **Row values** | `Comments.screenshot_url` (absolute URLs on the old host) | `UPDATE … replace()` | §11.3 |
| **Row values** | `Comments.page_url`, `route`, `page_title`, `selector`, `snapshot` (captured from customer pages) | **leave as-is** — historical evidence; rewriting falsifies the record | §11.3 |
| **Row values** | seeded `Plans.name`, `PredefinedActions.*`, demo content | review each hit | §11.3 |

The brand at this layer lives in **data, not schema** — which is why §11.3 is `UPDATE` statements and
not migrations.

### 11.3 Row-value migration

All identifiers below are the **physical** snake_case names (§11.1), not the CLR property names.

| Data | Where | Action |
|---|---|---|
| Product name / branding | `app_settings` rows read by `BrandingService` | Inspect first: `SELECT id,key,value FROM app_settings WHERE value ILIKE '%pointer%' OR key ILIKE '%pointer%';` then `UPDATE app_settings SET value='<NAME_DISPLAY>' WHERE key ILIKE '%productname%';` |
| Email From-name / From-email | `app_settings` | Update to the new sender. **These DB rows override the env defaults** (§10.5) — miss them and mail keeps going out under the old brand |
| Branding URLs (site, docs, support) | `app_settings` | New domain |
| Project keys containing the brand | `projects.key` = `pointer-api`, `pointer-landing` (Q23) | A key rename simultaneously invalidates: the widget snippet in that app, the per-project upload directory, and every stored screenshot URL. **Do all four in one transaction or none of them** |
| Upload directories | `wwwroot/uploads/<project-key>/` (today `uploads/pointer-api`) | `mv` inside the `uploads` docker volume, together with the key rename |
| Captured element payload | **`comments.element`** — a JSON *text* column holding the whole `ElementCapture` (`ScreenshotUrl`, `PageUrl`, `Selector`, `Snapshot`, `Classes`, `ComputedStyles`, `AppliedCssRules`, `SourcePath`, `ParentInfo`, `Route`, `PageTitle`, …) | See §11.4 — a blind `replace()` on this column rewrites the historical page URLs too |
| Page-context snapshots | `page_context_snapshots` (+ `comments.page_context_snapshot_id`) | Inspect for absolute URLs before deciding; same JSON caution applies |
| Picked actions / prompts | `comments.picked_actions`, `picked_action_text`, `picked_action_prompt` | Review for brand text in stored prompts |
| Seeded plan / action / status names | `plans`, `predefined_actions`, `predefined_action_suggestions`, `status_presentations` | `SELECT` for `%pointer%` and review each hit |
| Demo content | seeded demo comments/projects (`DemoService.cs:181`) | Re-seed rather than patch, if the demo tenant is disposable |
| API keys / tokens | `users`, api-key rows | No brand content. **Do not rotate as part of the rename** — unrelated blast radius |

### 11.4 The `comments.element` JSON column — do not blanket-replace

The screenshot URL you *want* to fix and the page URL you *must not* touch live in the same JSON blob.

**Preferred: don't rewrite anything.** If the old `api.` host keeps proxying to the new one (§9),
every stored screenshot URL keeps resolving and this migration is unnecessary. Only rewrite when you
actually retire the old host.

If you must rewrite, target the single key — never the whole column:

```sql
-- 0. Confirm the JSON key casing FIRST (serializer settings decide Pascal vs camel):
SELECT element::jsonb ?? 'ScreenshotUrl' AS pascal, element::jsonb ?? 'screenshotUrl' AS camel
  FROM comments WHERE element IS NOT NULL LIMIT 1;

-- 1. How many rows are affected:
SELECT count(*) FROM comments
 WHERE element::jsonb->>'ScreenshotUrl' LIKE '%api.pointer.moamen.work%';

-- 2. Rewrite ONLY that key (inside a transaction, on a restored backup first):
BEGIN;
UPDATE comments
   SET element = jsonb_set(
         element::jsonb, '{ScreenshotUrl}',
         to_jsonb(replace(element::jsonb->>'ScreenshotUrl',
                          'api.pointer.moamen.work', '<HOST_API>')))::text
 WHERE element::jsonb->>'ScreenshotUrl' LIKE '%api.pointer.moamen.work%';
-- 3. Verify a sample renders in the dashboard, THEN commit:
SELECT id, element::jsonb->>'ScreenshotUrl' FROM comments ORDER BY id DESC LIMIT 5;
COMMIT;   -- or ROLLBACK;
```

If project keys were also renamed (Q23), the path segment inside the same URL needs a second
`replace(... , '/uploads/pointer-api/', '/uploads/<new-key>/')` — do it in the same statement.

**`element::jsonb->>'PageUrl'`, `Route`, `PageTitle`, `Selector`, `Snapshot` stay untouched.** They
record what the reporter's browser actually saw; rewriting them falsifies the evidence a developer
uses to locate the element.

### 11.5 Sweep for anything missed

```sql
-- every text-ish column in the schema, then check each for brand values
SELECT table_name, column_name, data_type FROM information_schema.columns
 WHERE table_schema='public' AND data_type IN ('text','character varying','jsonb','json')
 ORDER BY table_name, column_name;
-- per candidate:  SELECT count(*) FROM <t> WHERE <c>::text ILIKE '%pointer%';
```

Generate the per-column checks mechanically rather than by hand:

```sql
SELECT format('SELECT %L AS col, count(*) FROM %I WHERE %I::text ILIKE ''%%pointer%%'';',
              table_name||'.'||column_name, table_name, column_name)
  FROM information_schema.columns
 WHERE table_schema='public' AND data_type IN ('text','character varying','jsonb','json');
```

Run the generated statements, and record the counts in `docs/rebranding/CHANGELOG-rebrand.md` — both
before and after, so the data migration is auditable.

---

## 12. Verification

### 12.1 The acceptance gate

`verify-no-pointer.sh` ships next to this file. Run it from each repo root. It does three things:

1. **Pass A — occurrences that spell the brand.** `grep -rniE 'pointer|poitner'` (case-**insensitive**
   on purpose: an explicit casing list misses `pOinter`), then drops any line whose *only* brand hits
   are protected DOM/CSS tokens (§7.2).
2. **Pass B — residue that does not spell the brand** (§5.11). Opt in per repo:
   ```bash
   EXTRA_BRAND_PATTERN='pf-|ptr_|moamen\.work' ./verify-no-pointer.sh
   ```
   Set it to only what you decided to rename. Pass B is plain `grep -E` — it does **not** go through
   the protected-token filter.
3. **Filename scan.** `find -iname '*pointer*' -o -iname '*poitner*'` — a content grep can never flag
   a brand-named *file*, and never reads binaries at all (`pointer-ext-v0.1.0.zip`,
   `store-assets/pointer-*.jpg`, `Pointer.sln`).

`--protected` additionally asserts the DOM/CSS token count has not dropped below `BASELINE.txt`
(exit 2), which is how you catch a rename that ate `pointer-events`.

**Verified behaviour** (tested on synthetic fixtures, 2026-09-09): a finished rename containing
`cursor: pointer`, `pointer-events: none`, `addEventListener('pointerdown')`, a `// COMPAT: remove
<date>`-tagged legacy storage read, and this `docs/rebranding/` directory → **PASS**. Adding one
`.pf-launcher` rule and one `pointer-icon.svg` file → **FAIL**, with both listed. Two bugs were found
and fixed by that test: POSIX `awk` has no `\b` support (so the protected pattern is written without
word boundaries, and Pass B runs in grep instead), and the script was flagging its own filename.

Known limitation: the protected-token stripping is line-based. A line containing *both* a protected
token and real brand residue is kept (correct), but a line whose residue is *inside* a protected-looking
token would be dropped — no such case exists today, and Pass B covers the shapes that matter.

**Allowlist (the only permitted survivors):**

1. The DOM/CSS vocabulary in §7.2 (147 occurrences today).
2. `docs/rebranding/*` — this plan, the reviews, `BASELINE.txt`, `CHANGELOG-rebrand.md` (they *must*
   name the old brand).
3. Deliberate compatibility aliases (§9) — each one commented `// COMPAT: remove <COMPAT_UNTIL>`.
   The script requires that comment; an alias without it fails the gate.
4. The EF migration ID in §4.1, **if** the human chose not to run the history-table SQL.
5. `COMPOSE_PROJECT_NAME=pointer-api` / `name: pointer-api`, **if** option 10.3A was chosen.
6. Historical `docs/` files, **if** the §5.10 policy said allowlist rather than rewrite.
7. Lockfiles' integrity entries for the deprecated packages, until the next `npm i` regenerates them.
8. `verify-no-pointer.sh` itself — its own patterns necessarily spell the old brand (it self-excludes).
9. Anything you decided in §5.11 to **keep** (`pf-`, `ptr_`) — recorded there with the reason, and
   left out of `EXTRA_BRAND_PATTERN`.

Anything else is a bug. `git log` and CHANGELOG entries are exempt (history is not code).

### 12.2 Build and test gates

**Clean generated output first.** `obj/` and `bin/` hold `Pointer.*.dll`, `Pointer.*.AssemblyInfo.cs`,
`*.deps.json`, and `*.sourcelink.json` — the last one embeds the **old GitHub repo URL**. Stale
artifacts make the grep gate fail (or, worse, pass while the real build still emits old assembly
names). Same for `node_modules/`, `dist/`, `.angular/`, and the three dashboards' lockfiles.

```bash
dotnet clean && find . -type d \( -name obj -o -name bin \) -not -path "*/node_modules/*" -exec rm -rf {} +
```

```bash
# API repo
dotnet build && dotnet test                      # must match the phase-1 baseline exactly
dotnet ef migrations list                        # no pending migration against prod
bash -n API/wwwroot/${NAME_LOWER}.sh             # CLI still parses
# widget
(cd web-component && npm ci && npm run build)
# dashboards
for a in angular react vue; do (cd $a && npm ci && npm run build) || echo "FAIL $a"; done
(cd angular && npm test -- --watch=false)        # must match the phase-1 baseline
# grep gates
./docs/rebranding/verify-no-pointer.sh
```

### 12.3 Functional scenarios — all five must pass against the deployed stack

1. **New contract, end to end.** A scratch page loads `${HOST_API}/${NAME_LOWER}.js` with
   `<${NAME_KEBAB}-feedback project="…" server="…">`, picks an element, submits a comment; the
   comment (with screenshot, selector, computed styles) appears in all three dashboards.
2. **Installer + skills.** `curl -fsSL ${HOST_API}/install.sh | sh` in an empty repo installs
   `${NAME_KEBAB}-init` and `${NAME_KEBAB}-feedback`, scaffolds `.${NAME_LOWER}/credentials.env`, and
   `./.${NAME_LOWER}/${NAME_LOWER}.sh list` returns the comment from (1). Then an AI agent, given only
   the new skill, applies that comment to source and marks it completed.
3. **No forced logout.** A browser session created before the deploy stays signed in; language and
   theme survive; the demo panel does not reappear.
4. **Email delivers.** Invite a user; the mail arrives from the new sender, with the new product name
   in the subject and body, and passes SPF/DKIM (check raw headers).
5. **Old contract (only if `LIVE_INSTALLS=yes`).** An untouched page with `<pointer-feedback>` +
   `/pointer.js` still posts a comment; a repo with `.pointer/credentials.env` and the old skill still
   lists comments. Both log the deprecation notice.

**Use the e2e harness as the cheapest proof of (1) and (5).** `e2e/` already automates the whole
customer contract end to end (fixture apps embedding the widget, a Playwright widget spec, and
AI-agent cases that install the skills and apply a comment). Update the fixtures to the new contract
and run `e2e/run-e2e.sh`; if `LIVE_INSTALLS=yes`, keep **one** fixture app on the old tag + old
loader path so the compat layer is regression-tested on every run, not just once by hand.

Plus: all seven hostnames 200 over TLS; `/api/branding` returns the new product name and the
dashboards' chrome reflects it; screenshots from before the rename still render.

---

## 13. Cutover, rollback, and the deprecation clock

### 13.1 Cutover order (one maintenance window)

1. Announce the window. Back up (§8.0) and **verify the backup restores** into a scratch DB.
2. Deploy the renamed API (new host live, old host proxying).
3. Run §4.1's history SQL (if chosen) and §11's data updates, in a transaction, from the backup point.
4. Deploy the three dashboard builds.
5. Publish the extension listing update (async review — it will lag; that is fine).
6. Run §12 in full. Only then announce publicly.

### 13.2 Rollback

| Failure | Rollback |
|---|---|
| API broken after deploy | `git checkout <pre-rebrand-tag>` on the VM, `docker compose up -d --force-recreate api` |
| Data migration wrong | Restore the `pg_dump` (`pg_restore -c`); uploads from the tarball |
| Volumes came up empty (§10.3) | **Stop immediately** — do not let migrations run further. The old volumes still exist: `docker volume ls`, re-point the project name, restart |
| Users logged out | Ship the §4.3 fallback as a hotfix; sessions in `localStorage` are recoverable only if the old key was not deleted — so **never delete-then-write**, always write-then-delete |
| Emails not delivering | Revert `EMAIL_FROM_*` to the old verified sender (env + the DB `AppSetting` rows) |
| npm packages wrong | Publish a corrected patch; the old packages are still there (never unpublished) |

Tag the pre-rebrand commit in every repo: `git tag pre-rebrand-$(date +%F) && git push --tags`.

### 13.3 Deprecation clock

On `COMPAT_UNTIL`: remove every `// COMPAT:` alias (§9), delete the old Caddy host blocks and DNS
records, drop the storage-key fallbacks, and re-run §12 — the allowlist should shrink to items 1, 2,
and possibly 4/5/6.

---

## 14. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Blind `s/pointer//g` renames `pointer-events` / `cursor-pointer` / `pointerdown` | **High** — it is the obvious first move | Broken picker, dead cursors in 3 apps | §7.2 protected list + before/after count assertion |
| R2 | VM directory rename orphans the docker volumes | **High** if the dir is renamed casually | Looks like total data loss; migrations run on an empty DB | §10.3 — pin the project name or migrate volumes deliberately |
| R3 | Storage keys swapped without a fallback | High | Every user logged out, prefs reset | §4.3 migrate-on-read, verified by scenario 12.3.3 |
| R4 | EF migration ID renamed without the history SQL | Medium | A migration re-runs against production data | §4.1, then `dotnet ef migrations list` |
| R5 | Brevo sender domain unverified at cutover | Medium | Invites/resets silently stop | §10.5, start on day 1, verify with raw headers |
| R6 | `clients/*/package.json` `repository.url` still points at the old repo | Medium | `npm publish` 403; no clients ship | §8.1 gotcha, in the same commit as the workflow |
| R7 | First client publish auto-bumps to `0.0.1` | Medium | Apps resolve a nonsense version | §8.6 — pass explicit `version` on the first run |
| R8 | Customer installs break (old tag / old skill paths) | High if `LIVE_INSTALLS=yes` | Silent breakage in someone else's app; support load | §9 dual support with dated removal |
| R9 | Extension `host_permissions` still list the old domain | Medium | Proxy silently fails; capture looks broken | §8.8, tested unpacked |
| R10 | Three dashboards drift apart during the rename | Medium | Parity rule violated; divergent UX | One agent per app, same brief, diff the results (§8.7) |
| R11 | The `poitner` typo survives (43 + 9 occurrences) | Medium | "Zero pointer" gate passes while the misspelling remains | Replacement set 1 runs **first**; the gate greps `poitner` too |
| R12 | Historical `docs/` churn buries the real diff | Low | Review fatigue; the meaningful changes get rubber-stamped | Decide §5.10 up front; do docs in their own commit |
| R13 | Trademark conflict discovered after announcement | Low | Forced second rename | Screen before cutover (§6.1) |
| R14 | Screenshot URLs stale after project-key rename | Medium | Comment evidence 404s | §11 — rename key + dir + URLs together, verify a sample |
| R16 | `JWT__Issuer` renamed in one deploy | **High** — it looks like a harmless string | **Every** session 401s at once; the dashboard's request-storm path is exactly this trigger | §4.6 — freeze it, or dual-accept across three deploys |
| R17 | `Pointer` config section renamed in only 1–2 of its 3 places | High | Swagger widget embed silently disables (`GetValue("Enabled", false)`) | §4.7 — one commit, verify `/swagger` |
| R18 | CORS `dashboardOrigins` not updated | High | Renamed dashboards blocked on all privileged calls | §8.2 — add new, keep old until cutover |
| R19 | `injectedFiles` HashSet not updated with the new filenames | Medium | Served skills ship a literal `<POINTER_SERVER>`; installs land unconfigured | §8.4 — curl-grep gate |
| R20 | `pf-`/`--pf-*` renamed without a fallback | Medium (if `LIVE_INSTALLS=yes`) | Every customer's widget theme reverts to defaults, silently | §5.11 / §8.3 — nested `var()` fallback |
| R21 | `ptr_` prefix changed without dual acceptance | Medium | Every deployed AI agent's API key stops authenticating | Q27 — accept both prefixes; never filter on prefix |
| R22 | Brand residue that doesn't spell "pointer" is never audited | **High** — the gate cannot see it | Ships "rebranded" with `pf-`, `ptr_`, old artwork, and `moamen.work` intact | §5.11 — explicit decisions + `EXTRA_BRAND` |
| R15 | `demo.` flow still seeds the old brand | Medium | New users' first experience shows the old name | `DemoService.cs:181` + seeded demo data in §11 |

---

## 15. Appendix — the identifier map (fill in and keep)

| Old | New | Kind | Where |
|---|---|---|---|
| `Pointer` | `${NAME_DISPLAY}` | product name | copy, emails, titles |
| `Pointer.{API,Application,Domain,Infrastructure,Tests}` | `${NAME_PASCAL}.…` | C# namespace + assembly | 5 projects, ~430 files |
| `Pointer.sln` | `${NAME_PASCAL}.sln` | file | root |
| `PointerUrlResolver` | `${NAME_PASCAL}UrlResolver` | C# class | `API/Extensions/` |
| `pointer-feedback` | `${NAME_KEBAB}-feedback` | custom element | widget, install guide, docs |
| `pointer-feedback-hl` / `-hl-style` | `${NAME_KEBAB}-feedback-hl…` | CSS class / element id | widget |
| `/pointer.js`, `/pointer.css`, `/pointer.sh` | `/${NAME_LOWER}.{js,css,sh}` | served asset | `API/wwwroot/` |
| `pointer-init.md`, `skill.md` | `${NAME_KEBAB}-{init,feedback}.md` | served skill | `API/wwwroot/` |
| `pointer-init`, `pointer-feedback` | `${NAME_KEBAB}-{init,feedback}` | skill name + dir | customer repos |
| `.pointer/` | `.${NAME_LOWER}/` | config dir | customer repos, both our repos |
| `__POINTER_CONFIG__`, `__POINTER_FETCH__` | `__${NAME_UPPER}_…__` | JS global | widget |
| `POINTER_{SERVER,PROJECT,ENV,ENABLED,API_KEY,AI_TOOL}` (+ `VITE_`/`REACT_APP_`/`NEXT_PUBLIC_`) | `${NAME_UPPER}_…` | env var | widget, CLI, skills, host apps |
| `pointer_{token,user,visible,toolbar_pos,page_session_id,env_*}` | `${NAME_SNAKE}_…` | storage key | widget |
| `pointer_admin_{token,user,lang,theme}` | `${NAME_SNAKE}_admin_…` | storage key | Angular + static admin |
| `pointer_{token,user,lang,theme}` | `${NAME_SNAKE}_…` | storage key | React, Vue |
| `pointer_demo`, `pointer_demo_dismissed` | `${NAME_SNAKE}_…` | storage key | all three dashboards |
| `pointer_install_{seen,suppressed,shown_session}` | `${NAME_SNAKE}_install_…` | storage key | Angular |
| `pointer_via_proxy__` | `${NAME_SNAKE}_via_proxy__` | storage key | extension |
| `@moamen-ui/pointer-{angular,react,vue}` | `${PKG_*}` | npm package | 74 import sites + 3 `package.json` |
| `pointer-api-clients` | `${NAME_LOWER}-api-clients` | npm package (private) | root `package.json` |
| `poitner-api`, `pointer-dashboard` | new repo names | GitHub repo | remotes, workflow, docs, VM |
| `*.pointer.moamen.work` (7 hosts) | `*.${DOMAIN}` | hostname | Caddyfile, env files, tests, docs, extension |
| `pointer` (db + role) | `${NAME_LOWER}` | Postgres identity | compose, justfile, connection string |
| `pointer-api_{pgdata,uploads}` | project-name-derived | docker volume | §10.3 |
| `pointer-api-{api,db,caddy}-1` | project-name-derived | container | DEPLOY.md |
| `~/pointer-api`, `~/pointer-dashboard` | `~/${NAME_LOWER}-…` | VM path | Caddyfile roots, DEPLOY.md |
| `uploads/pointer-api` | per new project key | upload dir + stored URLs | §11 |
| `Pointer Feedback` | `${NAME_DISPLAY}` | extension name | `manifest.json`, store listing |
| `pf-` (175 classes, 1,020 uses) + `--pf-*` tokens | Q26 | widget CSS class + public theming prefix | `web-component/src/**`, built assets, extension |
| `ptr_` | Q27 | API-key prefix | `ProfileService.cs:73`, tests, skills |
| `pointer-api` (JWT `iss`/`aud`) | §4.6 | token issuer | compose, `JwtTokenService.cs:10` |
| `Pointer` (config section) | §4.7 | config key + `GetSection` string + `Pointer__*` env | `appsettings.json`, `Program.cs:180`, compose |
| `<POINTER_SERVER>` | `<${NAME_UPPER}_SERVER>` | served-file placeholder | `Program.cs:212` + `wwwroot/*.md` |
| `/pointer-init.md`, `/skill.md`, `/install.sh` in `injectedFiles` | new filenames | hardcoded served-path set | `Program.cs:197-201` |
| `window.__pointerEmbedded` | `window.__${NAME_LOWER}Embedded` | embed guard flag | `Program.cs:283-319` |
| 5 × `*.pointer.moamen.work` CORS origins | new hosts | hardcoded allow-list | `Program.cs:61-68` |
| `admin@pointer.local` | new seed admin email | seeder input | `.env.example:5`, `.env.prod.example:12` |
| `poitner-api` (docs URL) | new repo URL | customer-facing link | `BrandingService.cs:15` |
| `20260827124245_ReassignPointerLandingOwnership` | §4.1 | EF migration ID | **frozen unless the SQL runs** |
