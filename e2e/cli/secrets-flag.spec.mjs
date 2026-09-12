// E2E spec for R2-06: Secrets / payload advisory flag — CLI + legacy pointer.sh layer.
// Covers:
// - R2-06-02 ⛓: flag absent from `pointer get --json` and `./.pointer/pointer.sh get`
// Contract: docs/roadmap/testing/R2-06-tests.md
//
// Both commands hit GET /api/comments/{F} WITHOUT the X-Pointer-Client header, so the server
// gate already withholds the fields — what this row additionally proves is that the CLI's own
// AiCommentView whitelist and the legacy script's jq pass-through both stay flag-free.
import { test, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { get, login, BASE_URL } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, keys as loadKeys } from '../scripts/lib/state.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo } from '../scripts/lib/git.mjs';
import { installPointerSh } from '../scripts/lib/install.mjs';
import { PROJECT_KEY, countPayloadFlag, ensureFlaggedComment } from '../scripts/lib/secrets-flag.mjs';

// Read lazily: Playwright evaluates this file to DISCOVER tests, so an eager read on an
// unseeded workspace made `playwright test --list` report 0 tests in 0 files.
const credentials = () => loadCredentials();
const keys = () => loadKeys();

// The whitelist `pointer get` prints (cli/src/apply/projection.ts toAiCommentView) — asserting
// the exact key set is what makes "zero payloadFlag" a property of the PROJECTION, not luck.
const AI_COMMENT_VIEW_KEYS = [
  'appliedAt',
  'appliedByLabel',
  'authorName',
  'body',
  'commitUrl',
  'createdAt',
  'element',
  'environment',
  'id',
  'isBugReport',
  'pickedActions',
  'replies',
  // Added by R3-01 AC-5: `get --json` resolves element.sourcePath against .pointer/manifest.json.
  // It is a resolution of data the projection already carries, not a new field FROM the server, so
  // it cannot widen what this scenario guards — the payload-flag keys stay forbidden below.
  'resolvedSource',
  'status',
].sort();

test('R2-06-02 — flag: absent from pointer get --json and pointer.sh get', async () => {
  const qa = await login(credentials().tester.email, credentials().tester.password);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
  const wsAdminKey = keys().wsAdmin.apiKey;

  // F: the flagged comment (find-or-create — the cli phase runs before the widget phase that
  // owns R2-06-01; see secrets-flag.mjs). Fetch its CURRENT body so the "actually fetched F"
  // assertion below holds whatever body state the earlier phases left it in.
  const F = await ensureFlaggedComment(qa.token);
  const currentBody = (await get(`/api/comments/${F}`, { token: qa.token })).body;
  expect(currentBody, 'fixture comment must have a body').toBeTruthy();

  const repo = tempRepo();
  try {
    // 1. Temp repo: .pointer/config.json (project e2e-alpha) + credentials.env with the WA key.
    // The env exports below mirror the doc: pointer.sh reads POINTER_API_KEY from the
    // environment first, and install.sh writes credentials.env EMPTY — without one of these the
    // legacy path cannot authenticate.
    mkdirSync(join(repo.dir, '.pointer'), { recursive: true });
    writeFileSync(
      join(repo.dir, '.pointer', 'config.json'),
      JSON.stringify({ project: PROJECT_KEY, server: BASE_URL }, null, 2),
    );
    writeFileSync(join(repo.dir, '.pointer', 'credentials.env'), `POINTER_API_KEY=${wsAdminKey}\n`);
    const cliEnv = {
      POINTER_SERVER: BASE_URL,
      POINTER_PROJECT: PROJECT_KEY,
      POINTER_API_KEY: wsAdminKey,
    };

    // 2. pointer get <F> --json → exit 0, zero payloadFlag substrings, AiCommentView key set.
    const cli = await spawnCli({ cwd: repo.dir, args: ['get', String(F), '--json'], env: cliEnv });
    expect(cli.code, `pointer get must exit 0: ${cli.stderr}`).toBe(0);
    expect(countPayloadFlag(`${cli.stdout}${cli.stderr}`), 'CLI get output must be flag-free').toBe(0);
    expect(cli.json, 'pointer get --json must print valid JSON').toBeTruthy();
    expect(Object.keys(cli.json).sort(), 'printed keys must match the AiCommentView whitelist').toEqual(AI_COMMENT_VIEW_KEYS);

    // 3. Legacy pointer.sh via the REAL install.sh (fresh temp repo ⇒ fresh .pointer/.token_cache,
    // so a stale JWT from another scenario can never misread as "no flag").
    const install = installPointerSh(repo.dir);
    expect(install.code, `install.sh must exit 0: ${install.stderr}`).toBe(0);
    expect(existsSync(join(repo.dir, '.pointer', 'pointer.sh')), 'install.sh must place .pointer/pointer.sh').toBe(true);

    const sh = spawnSync('bash', ['.pointer/pointer.sh', 'get', String(F)], {
      cwd: repo.dir,
      encoding: 'utf8',
      timeout: 60_000,
      env: { ...process.env, ...cliEnv },
    });
    expect(sh.status, `pointer.sh get must exit 0: ${sh.stderr}`).toBe(0);
    expect(sh.stdout, 'pointer.sh get must contain the comment body (proves it fetched F)').toContain(currentBody);
    expect(countPayloadFlag(`${sh.stdout}${sh.stderr}`), 'pointer.sh get output must be flag-free').toBe(0);

    // 4. Both zero — the row's grep -c payloadFlag evidence pair (0 and 0).
    expect(countPayloadFlag(`${cli.stdout}${cli.stderr}`)).toBe(0);
    expect(countPayloadFlag(`${sh.stdout}${sh.stderr}`)).toBe(0);
  } finally {
    repo.cleanup();
  }
});
