# R1-03 — Dashboard quick-start prints the pre-filled CLI command (§2a · Release 1 · 1 day)

## Goal
The dashboard's install guide shows **one copy-pasteable command with the user's API key and the
selected project already filled in** — `npx -y pointer-feedback init --server … --key ptr_… --project …`
— so the terminal step needs no second trip to the profile page. `curl | sh` stays available as
"alternative (no Node)".

## Out of scope
- Device-code login (§2b, held). Any API change (the key already comes from `GET /api/me/api-key`).
- React/Vue dashboard variants (Angular is the shipped one; mirror later if they are still maintained).

## Prerequisites
- R1-02 published to npm as `pointer-feedback` (or at least the flag surface fixed as in R1-02 §B — the dashboard can ship before npm publish since the command is only text).
- Repo: `pointer-dashboard` (Angular 22). Files verified 2026-09-11:
  - `angular/src/app/shared/install-guide/install-guide.component.ts` — `buildGuideSteps()` (`:79-108`) builds the primary steps; step 1 today is `curl -fsSL ${server}/install.sh | sh` (`:93`); credentials step uses `credentialsSnippet()` (`:111-123`); the wizard's curl block is at `:380-396`; API key comes from `getApiMeApiKeyResource()` (`:702`) into `stepsInput` (`:762-766`).
  - i18n: `angular/public/assets/i18n/en.json` keys `demo.step3Title/step3Hint` (`:184-185`), `install.wizard.curlTitle/curlHint`; Arabic mirror in `ar.json`.
  - Tests: `install-guide.spec.ts`, `install-guide.service.spec.ts`.

## Design
- New pure helper in `install-guide.component.ts` (exported for tests):
  ```ts
  export function initCommand(i: { server: string; apiKey: string | null; projectKey: string | null; environment?: 'local'|'staging'|'production' }): string {
    const parts = ['npx -y pointer-feedback init', `--server ${i.server}`];
    if (i.apiKey) parts.push(`--key ${i.apiKey}`);
    if (i.projectKey) parts.push(`--project ${i.projectKey}`);
    if (i.environment) parts.push(`--environment ${i.environment}`);
    return parts.join(' ');
  }
  ```
  When `apiKey` is null (not generated yet) the command omits `--key` and the hint says "generate your key in Profile → API key or the CLI will ask for it".
- `buildGuideSteps().primary` becomes:
  1. `install.stepInitTitle` / `install.stepInitHint` — `code: initCommand(...)` **(new first step)**
  2. existing `demo.step4` credentials step is **removed** when `apiKey` is present (the command carries it); kept for demo sessions (`demo != null`, email/password flow unchanged).
  3. existing agent-prompt step `install.stepAgent*` reworded: "Only if the CLI told you to (Next.js/Angular/monorepo): run the pointer-init skill" (hint key text change only).
  4. `demo.step5`, `demo.step6` unchanged.
  `manual` array: add a third entry `{ titleKey: 'install.stepCurlTitle', hintKey: 'install.stepCurlHint', code: \`curl -fsSL ${server}/install.sh | sh\` }` labelled "Alternative without Node".
- Wizard curl block (`:380-396`): replace the hard-coded curl `<pre>` and its copy button with the `initCommand()` output; add a collapsed "No Node? use curl" line under it.
- **Secret handling**: the key is already rendered in this component today (`credentialsSnippet`), so no new exposure. Mask the key in the visible `<code>` as `ptr_••••••••` with a "reveal" toggle; **copy always copies the full command**. Decision: mask by default.
- i18n keys to add (en + ar): `install.stepInitTitle` ("Run the installer in your project"), `install.stepInitHint` ("One command: installs the skills, stores your key, picks the project and mounts the widget for Vite/static apps. Needs Node 18+. The `-y` skips npx's first-run prompt."), `install.stepCurlTitle` ("Alternative without Node"), `install.stepCurlHint` ("Installs the skills only; you fill the key by hand."), `install.wizard.reveal` ("Reveal key"), `install.wizard.hide` ("Hide key"); update `install.stepAgentHint`.

## Tasks
1. Add `initCommand()` + unit tests (`install-guide.spec.ts`): with/without key, with/without project, environment flag, masking helper.
2. Rework `buildGuideSteps()` per Design; update snapshot/expectation tests that assert the first step is curl.
3. Update the wizard template block (`:380-396`) — masked command, copy button copies full text, reveal toggle, curl fallback line.
4. Add i18n keys to `en.json` and `ar.json`; run the i18n key-parity test if one exists (grep `i18n` in `angular/src/**/*.spec.ts`).
5. `npm run build` and `npm test` in `angular/`.
6. Manual: login → Projects → open install guide → command shows masked key → copy → paste into a terminal → the pasted text contains the real key.

## Dashboard tasks
This doc **is** dashboard work. No API changes; no Orval regen.

## Tests
- Unit: `install-guide.spec.ts` (new `initCommand` cases, steps order, mask/reveal), existing suites green.
- E2E scenario name (R2-00): `quickstart-copies-prefilled-command`.

## Acceptance criteria
- [ ] First primary step shows `npx -y pointer-feedback init --server <server> --key ptr_•••••••• --project <key>`; copy yields the full unmasked command.
- [ ] Without a generated key the command omits `--key` and the hint explains it.
- [ ] Demo sessions still show email/password credentials.
- [ ] curl command still reachable under "Alternative without Node".
- [ ] Arabic strings present; RTL layout unaffected.
- [ ] `npm test` green.

## Rollout / compatibility
Text-only change; safe to deploy before the npm package exists (the command fails with a clear npm 404 until publish — coordinate deploy with R1-02 publish).

## Report template
Files changed · test summary · screenshot of the guide (masked and revealed).
