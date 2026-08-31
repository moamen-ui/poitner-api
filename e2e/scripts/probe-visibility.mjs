// Pure-API, zero-AI checks: role-visibility matrix, private-comment exclusion on BOTH the list
// and admin apply-queue surfaces, cross-project isolation, and ?environment= filtering. Must run
// (and pass) right after seed.mjs, before any AI-under-test invocation — if these fail, the AI
// cases run against an invalid scenario and their results mean nothing.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, login, ApiError } from './lib/api.mjs';
import { PROJECTS } from './lib/constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const expected = JSON.parse(readFileSync(join(STATE_DIR, 'expected.json'), 'utf8'));
const credentials = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));

let pass = 0;
let fail = 0;
function check(label, condition) {
  if (condition) {
    pass++;
    console.log(`  ok    ${label}`);
  } else {
    fail++;
    console.error(`  FAIL  ${label}`);
  }
}
function setEqual(actual, expectedIds) {
  const a = new Set(actual);
  const e = new Set(expectedIds);
  return a.size === e.size && [...e].every((id) => a.has(id));
}
function setExcludes(actual, excludedIds) {
  const a = new Set(actual);
  return excludedIds.every((id) => !a.has(id));
}

async function listComments(token, projectKey, query = '') {
  const res = await get(`/api/projects/${projectKey}/comments${query}`, { token });
  return res.items.map((c) => c.id);
}

async function main() {
  const c = expected.comments;

  console.log('==> Logging in as every seeded role');
  const tokens = {};
  for (const [key, creds] of Object.entries(credentials)) {
    if (key === 'superAdmin') continue; // super-admin can't reach any of these endpoints meaningfully
    tokens[key] = (await login(creds.email, creds.password)).token;
  }

  const nonPrivateAlphaIds = [c.c1.id, c.c2.id, c.c3.id, c.c4.id, c.c5.id, c.c7.id, c.c8.id];

  console.log('==> TC-visibility: role matrix on e2e-alpha');
  // wsAdmin is deliberately excluded here — they authored C6, so their fetch is the one case that
  // must INCLUDE it (checked separately below), unlike every other non-author role.
  for (const role of ['deputy', 'developer', 'pm', 'tester']) {
    const ids = await listComments(tokens[role], PROJECTS.alpha.key, '?pageSize=100');
    check(`${role} sees the 7 non-private comments, excludes C6`, setEqual(ids, nonPrivateAlphaIds));
  }
  {
    // wsAdmin authored C6 — private is author-visible, so their own fetch must include it too:
    // the 7 non-private comments PLUS C6 (8 total).
    const ids = await listComments(tokens.wsAdmin, PROJECTS.alpha.key, '?pageSize=100');
    check('wsAdmin (C6 author) sees the 7 non-private comments plus C6', setEqual(ids, [...nonPrivateAlphaIds, c.c6.id]));
  }
  {
    const ids = await listComments(tokens.client, PROJECTS.alpha.key);
    check('client (QuickAccess) sees only own comments (C1, C8)', setEqual(ids, [c.c1.id, c.c8.id]));
  }

  console.log('==> TC-visibility: private exclusion on BOTH surfaces, no admin bypass');
  for (const role of ['deputy', 'developer', 'pm', 'tester']) {
    const ids = await listComments(tokens[role], PROJECTS.alpha.key, '?pageSize=100');
    check(`${role} does NOT see C6 (private, not the author) on the list endpoint`, !ids.includes(c.c6.id));
  }
  for (const role of ['wsAdmin', 'deputy']) {
    const unfiltered = await get(`/api/admin/projects/${PROJECTS.alpha.key}/apply-queue?pageSize=100`, { token: tokens[role] });
    const unfilteredIds = unfiltered.items.map((i) => i.id);
    check(`${role} does NOT see C6 on the admin apply-queue (regression check for the fixed bug)`, !unfilteredIds.includes(c.c6.id));

    const filtered = await get(`/api/admin/projects/${PROJECTS.alpha.key}/apply-queue?status=2&pageSize=100`, { token: tokens[role] });
    const filteredIds = filtered.items.map((i) => i.id);
    check(`${role}'s apply-queue with ?status=2 returns exactly {C1, C3, C4, C7}`,
      setEqual(filteredIds, [c.c1.id, c.c3.id, c.c4.id, c.c7.id]));
  }
  for (const role of ['developer', 'pm', 'tester']) {
    try {
      await get(`/api/admin/projects/${PROJECTS.alpha.key}/apply-queue`, { token: tokens[role] });
      check(`${role} is rejected from the admin apply-queue (non-admin role)`, false);
    } catch (err) {
      check(`${role} is rejected from the admin apply-queue (non-admin role)`, err instanceof ApiError && err.status === 403);
    }
  }

  console.log('==> TC-visibility: ?environment= filter');
  {
    const ids = await listComments(tokens.deputy, PROJECTS.alpha.key, '?environment=3');
    check('environment=3 (Production) returns exactly {C1, C3, C7}', setEqual(ids, [c.c1.id, c.c3.id, c.c7.id]));
  }
  {
    const ids = await listComments(tokens.deputy, PROJECTS.alpha.key, '?environment=1');
    check('environment=1 (Local) for a non-author excludes C6, returns {C4, C5, C8}', setEqual(ids, [c.c4.id, c.c5.id, c.c8.id]));
  }
  {
    const ids = await listComments(tokens.wsAdmin, PROJECTS.alpha.key, '?environment=1&pageSize=100');
    check('environment=1 (Local) for the C6 author includes C6 too — {C4, C5, C6, C8}', setEqual(ids, [c.c4.id, c.c5.id, c.c6.id, c.c8.id]));
  }

  console.log('==> TC-isolation: cross-project');
  {
    const alphaIds = await listComments(tokens.deputy, PROJECTS.alpha.key, '?pageSize=100');
    check('e2e-alpha fetch never contains the BETA-only comment', !alphaIds.includes(c.betaOnly.id));
    const betaIds = await listComments(tokens.deputy, PROJECTS.beta.key, '?pageSize=100');
    check('e2e-beta fetch contains only the BETA-only comment', setEqual(betaIds, [c.betaOnly.id]));
  }

  console.log('==> Determinism: two consecutive fetches match');
  {
    const first = await listComments(tokens.deputy, PROJECTS.alpha.key, '?pageSize=100');
    const second = await listComments(tokens.deputy, PROJECTS.alpha.key, '?pageSize=100');
    check('two consecutive e2e-alpha fetches return identical id-sets', setEqual(first, second) && setEqual(second, first));
  }

  console.log(`\n==> ${pass} passed, ${fail} failed`);
  if (fail > 0) process.exit(1);
}

main().catch((err) => {
  console.error('probe-visibility.mjs crashed:', err);
  process.exit(1);
});
