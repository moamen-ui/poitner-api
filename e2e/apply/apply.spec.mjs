// Playwright CLI-layer specs for R2-01 (Apply core library + `pointer apply` / `--plan`).
// Scenarios implemented, in the BINDING order of R2-01-tests.md (01 → 02 → 05 → 03 → 06 → 04 → 07):
// - R2-01-01    — apply: plan makes no edits
// - R2-01-02 ⛓ — apply: separate commits
// - R2-01-05 ⛓ — apply: empty index → exit 1, no PATCH
// - R2-01-03 ⛓ — apply: single commit
// - R2-01-06 ⛓ — apply: get --json = exact AiCommentView key set
// - R2-01-04 ⛓ — apply: never pushes
// - R2-01-07 ⛓ — apply: non-admin developer falls back to summary view  (nightly tier)
//
// The contract calls for "a single ordered node:test file". This suite's runner wires the apply
// phase through scripts/pw.sh, which invokes `npx playwright test "(^|/)apply/"` — a node:test
// file is invisible to that (0 tests found ⇒ phase FAILs) and to `run-e2e.sh --only`, which
// dispatches single scenarios with `npx playwright test -g <id>`. The intent the contract is
// protecting — one file, strict sequential execution, declaration order preserved — is exactly
// what playwright.config.ts already guarantees (workers: 1, fullyParallel: false, one file ⇒
// tests run back-to-back in the order written), so this is a @playwright/test file. Reported as
// SPEC-CONFLICT in the task report.
//
// The rows below share ONE repo and ONE comment queue on purpose: R2-01-02 consumes id1,
// R2-01-05 needs id2/id3 still ready, R2-01-03 consumes the remaining two. Reordering the tests
// makes the fixture states contradictory (see R2-01-tests.md "Scenario order" and "Flake notes").
import { test, expect } from '@playwright/test';
import { readFileSync, writeFileSync, appendFileSync, mkdirSync, rmSync } from 'node:fs';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { raw, login, get, post, patch, ApiError, BASE_URL } from '../scripts/lib/api.mjs';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo, bareRemote, refsSnapshot, assertRefsUnchanged } from '../scripts/lib/git.mjs';
import { credentials, keys } from '../scripts/lib/state.mjs';
import { Status, Environment, TENANT_OWNER, USERS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const execFileAsync = promisify(execFile);

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = resolve(here, '..', 'state');
const APPLY_WORK_DIR = join(STATE_DIR, 'apply-work');
// The verbatim SECURITY check compares plan output against THIS source constant — the same file
// cli/test/security-text.test.ts pins against API/wwwroot/skill.md, so all three surfaces stay
// in lockstep (R2-01-tests.md, Covers AC-1).
const SECURITY_TEXT_SRC = resolve(here, '..', '..', 'cli', 'src', 'apply', 'security-text.ts');

const NON_ADMIN_NOTE = 'Note: predefined-action prompts need an admin key';

// AiCommentView whitelist (cli/src/apply/projection.ts). Sorted so a deepEqual against the
// sorted runtime keys reads as a set comparison and a diff names the offending key directly.
const TOP_LEVEL_KEYS = [
  'appliedAt', 'appliedByLabel', 'authorName', 'body', 'commitUrl', 'createdAt', 'element',
  'environment', 'id', 'isBugReport', 'pickedActions', 'replies', 'status',
];
const ELEMENT_KEYS = [
  'appliedCssRules', 'classes', 'deviceType', 'pageTitle', 'pageUrl', 'parentInfo', 'route',
  'selector', 'snapshot', 'sourcePath', 'viewportHeight', 'viewportWidth',
];
const REPLY_KEYS = ['authorName', 'body', 'isAi'];
const PICKED_ACTION_KEYS = ['prompt', 'text'];
// CommentSummaryDto (?view=summary) — the non-admin fallback shape R2-01-07 step 4 asserts.
const SUMMARY_KEYS = ['authorName', 'body', 'createdAt', 'environment', 'id', 'route', 'sourcePath', 'status'];
// R2-06 guarantee: these may exist on server DTOs but must never survive the CLI projection.
const FORBIDDEN_KEY_RE = /"(hasPayloadFlag|payloadFlags|authorId|ownerId|editedBy)":/;

// Fixture state shared by every row (built in beforeAll). The scenarios mutate it in sequence —
// that is the coupling the ⛓ markers in R2-01-tests.md document.
let repo;               // { dir, cleanup } from lib/git.mjs tempRepo()
let bareDir;            // path of the bare "origin" — the never-push witness
let projectKey;         // e2e-apply-<runId> — its own queue so e2e-alpha's stays untouched
let projectId;
let id1; let id2; let id3;
let snapBefore = '';    // refsSnapshot(bareDir) taken BEFORE any scenario runs
let waSession;          // Workspace Admin (tenant owner) login — comment status PATCHes etc.
let testerSession;      // the comment author (PM — see the rate-limit note where it is assigned)

const git = (cwd, args) => execFileAsync('git', args, { cwd, maxBuffer: 1024 * 1024 * 16 });

const countCommits = async (cwd) =>
  parseInt((await git(cwd, ['rev-list', '--count', 'HEAD'])).stdout.trim(), 10);

const headSha = async (cwd) => (await git(cwd, ['rev-parse', 'HEAD'])).stdout.trim();

const porcelain = async (cwd) => (await git(cwd, ['status', '--porcelain'])).stdout;

// sha256 of every TRACKED file — untracked .pointer/ state (token cache etc.) is deliberately
// out of scope: plan must not touch tracked content, and it may freely create cache files.
async function trackedHashes(cwd) {
  const { stdout } = await git(cwd, ['ls-files']);
  const hashes = {};
  for (const file of stdout.split('\n').filter(Boolean)) {
    hashes[file] = createHash('sha256').update(readFileSync(join(cwd, file))).digest('hex');
  }
  return hashes;
}

// Extracts the heading-scoped block: from `heading` up to (not including) the next `## ` line.
// Applied identically to plan stdout and to the source constant, so the byte-equal comparison
// scopes both sides the same way; trailing whitespace is trimmed on both because the newlines
// between prompt sections are assembly glue, not part of the SECURITY text.
function headingBlock(text, heading) {
  const start = text.indexOf(heading);
  if (start === -1) return null;
  const next = text.indexOf('\n## ', start);
  const block = next === -1 ? text.slice(start) : text.slice(start, next);
  return block.replace(/\s+$/, '');
}

// Reads cli/src/apply/security-text.ts and takes the exported string literal, per the contract
// ("read the file, take the string literal") — no import machinery, no build output.
function readSecurityTextConstant() {
  const src = readFileSync(SECURITY_TEXT_SRC, 'utf8');
  const m = src.match(/export const SECURITY_TEXT = `([\s\S]*)`;\s*$/);
  if (!m) {
    throw new Error(`could not extract SECURITY_TEXT template literal from ${SECURITY_TEXT_SRC}`);
  }
  // Unescape the literal the way Node evaluates it: \` \$ and \\ are the bare character, a
  // backslash before a newline is a line continuation (dropped), \n and \t are control chars.
  // The file currently only uses \` and the leading continuation; the rest keeps this honest
  // if new escapes are ever added.
  return m[1].replace(/\\([\s\S])/g, (_, c) => {
    if (c === '\n' || c === '\r') return '';
    if (c === 'n') return '\n';
    if (c === 't') return '\t';
    return c;
  });
}

const occurrences = (haystack, needle) => haystack.split(needle).length - 1;

// Every CLI invocation is pinned to its repo's own .pointer/credentials.env: readApiKey checks
// process.env.POINTER_API_KEY FIRST (cli/src/auth.ts), so an inherited variable would silently
// override the fixture's key. Empty string is falsy there, which restores file-only resolution.
const runCli = (cwd, args) => spawnCli({ cwd, args, env: { POINTER_API_KEY: '' } });

const sortedKeys = (obj) => Object.keys(obj).sort();

function saveEvidence(name, content) {
  mkdirSync(APPLY_WORK_DIR, { recursive: true });
  writeFileSync(join(APPLY_WORK_DIR, name), content, 'utf8');
}

test.beforeAll(async () => {
  const waCreds = credentials().wsAdmin || TENANT_OWNER;
  // The PM persona, not the tester. Comment creation is rate-limited per USER (30/min), and the
  // tester authors most of the suite's comments — by the time the apply phase runs, its budget can
  // already be spent, which surfaces here as an unrelated-looking 429. PM is otherwise idle.
  const testerCreds = credentials().pm || USERS.pm;
  waSession = await login(waCreds.email, waCreds.password);
  testerSession = await login(testerCreds.email, testerCreds.password);

  // 1. The apply fixture repo: git init + identity + src/ (git.mjs's tempRepo does exactly the
  //    Precondition list) + the initial commit the scenarios build on.
  repo = tempRepo();
  writeFileSync(join(repo.dir, 'README.md'), 'apply fixture\n', 'utf8');
  writeFileSync(join(repo.dir, 'src', '.keep'), '', 'utf8');
  // a.txt and b.txt are TRACKED from the start, with baseline content. The scenarios then dirty
  // them, and a tracked-but-modified file is the case that actually matters: `git commit -a` — the
  // realistic way an apply sweeps in work it was never asked to commit — picks up modifications to
  // tracked files and IGNORES untracked ones. Leaving these untracked would make R2-01-02's
  // "the commit must not sweep b.txt in" assertion pass against a commit style that has the bug.
  writeFileSync(join(repo.dir, 'src', 'a.txt'), 'baseline a\n', 'utf8');
  writeFileSync(join(repo.dir, 'src', 'b.txt'), 'baseline b\n', 'utf8');
  await git(repo.dir, ['add', 'README.md', 'src/.keep', 'src/a.txt', 'src/b.txt']);
  await git(repo.dir, ['commit', '-m', 'fixture init']);

  // 2. A bare remote added as origin. It is a LOCAL PATH, so commitUrlFor() treats it as an
  //    unknown host → null (the R2-01-02 cell of the commitUrl matrix); R2-01-03 re-points it
  //    at a github-shaped URL for the other cell.
  bareDir = bareRemote(repo.dir);

  // 3. A dedicated project keeps e2e-alpha's seeded queue (shared ground truth for other
  //    specs) untouched. On 409 (re-run) find it via the list — same pattern as widget.spec.ts.
  const runId = Math.random().toString(36).substring(2, 7);
  projectKey = `e2e-apply-${runId}`;
  let project;
  try {
    project = await post('/api/admin/projects', { key: projectKey, name: 'E2E Apply' }, { token: waSession.token });
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 409) throw err;
    const all = await get('/api/admin/projects', { token: waSession.token });
    project = all.find((p) => p.key === projectKey);
    if (!project) throw new Error(`${projectKey} conflicted but wasn't found via list — ${JSON.stringify(err.body)}`);
  }
  projectId = project.id;
  await patch(`/api/admin/projects/${projectId}`, { commitStyle: 2 }, { token: waSession.token });

  // 4. QA posts three comments; WA marks each ReadyToApply. Three is the minimum that lets the
  //    binding row order leave a genuinely-ready comment for every consumer row.
  const element = { selector: '.cta', route: '/', pageUrl: 'http://localhost/x' };
  const bodies = ['Make the CTA primary', 'Bump footer year to 2026', 'Tighten the hero spacing'];
  const ids = [];
  for (const body of bodies) {
    const created = await post(
      `/api/projects/${projectKey}/comments`,
      { body, environment: Environment.Local, element },
      { token: testerSession.token },
    );
    ids.push(created.id);
  }
  for (const id of ids) {
    await patch(`/api/comments/${id}`, { status: Status.ReadyToApply }, { token: waSession.token });
  }
  [id1, id2, id3] = ids;

  // 5. The repo's Pointer install: config + WA's seeded key + a stack file.
  const pointerDir = join(repo.dir, '.pointer');
  mkdirSync(pointerDir, { recursive: true });
  writeFileSync(join(pointerDir, 'config.json'), `${JSON.stringify({ server: BASE_URL, project: projectKey }, null, 2)}\n`, 'utf8');
  writeFileSync(join(pointerDir, 'credentials.env'), `POINTER_API_KEY=${keys().wsAdmin.apiKey}\n`, 'utf8');
  writeFileSync(join(pointerDir, 'stack.json'), `${JSON.stringify({ frontend: ['react'], backend: null }, null, 2)}\n`, 'utf8');

  // 6. The never-push baseline, asserted at the end of every scenario and re-asserted in afterAll.
  snapBefore = refsSnapshot(bareDir);
});

test('R2-01-01 — apply: plan makes no edits', async () => {
  const start = Date.now();

  const porcelain0 = await porcelain(repo.dir);
  const hashes0 = await trackedHashes(repo.dir);

  const sub = await runCli(repo.dir, ['apply', '--plan']);
  expect(sub.code).toBe(0);
  expect(sub.stdout).toContain('PLAN ONLY');
  // The `### #<id> — …` item heading, not a bare `#<id>` substring: with numeric ids one can be
  // a prefix of another, and the heading form is what the prompt actually renders.
  for (const id of [id1, id2, id3]) {
    expect(sub.stdout).toContain(`### #${id} — `);
  }
  expect(sub.stdout).toContain('AI RULES PRECEDENCE');
  expect(sub.stdout).toContain('UNTRUSTED DATA');

  // AC-1 verbatim half: the SECURITY block in plan output must be byte-equal to the
  // heading-scoped extraction of cli/src/apply/security-text.ts's exported constant, and must
  // carry the rewritten commit-authority bullet.
  const fromSource = headingBlock(readSecurityTextConstant(), '## ⚠️ SECURITY');
  const fromStdout = headingBlock(sub.stdout, '## ⚠️ SECURITY');
  expect(fromStdout, 'plan output must contain the ## ⚠️ SECURITY section').not.toBeNull();
  expect(fromSource, `${SECURITY_TEXT_SRC} must still export SECURITY_TEXT`).not.toBeNull();
  expect(fromStdout).toBe(fromSource);
  expect(fromStdout).toContain('git push');
  expect(fromStdout).toContain('is never permitted');

  expect(await porcelain(repo.dir)).toBe(porcelain0);
  expect(await trackedHashes(repo.dir)).toEqual(hashes0);
  assertRefsUnchanged(bareDir, snapBefore);

  saveEvidence('plan.txt', sub.stdout);
  record({
    id: 'R2-01-01', tier: 'PR', layer: 'cli', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: 'plan made no edits; SECURITY block byte-equal to security-text.ts; stdout saved to state/apply-work/plan.txt',
  });
});

test('R2-01-02 ⛓ — apply: separate commits', async () => {
  const start = Date.now();

  // 1. Separate commit style (already the fixture default; PATCHed again exactly as the row says).
  await patch(`/api/admin/projects/${projectId}`, { commitStyle: 2 }, { token: waSession.token });

  // 2. a.txt staged, b.txt left deliberately unstaged — the commit must not sweep it in.
  appendFileSync(join(repo.dir, 'src', 'a.txt'), 'fix1\n', 'utf8');
  await git(repo.dir, ['add', '--', 'src/a.txt']);
  appendFileSync(join(repo.dir, 'src', 'b.txt'), 'unrelated\n', 'utf8');

  const count0 = await countCommits(repo.dir);

  // 3. Mark id1 applied with a reply — one commit, one PATCH.
  const sub = await runCli(repo.dir, ['apply', '--mark', String(id1), '--reply', 'Applied CTA fix']);
  expect(sub.code).toBe(0);

  // 4. Server-side truth via WA reads. raw() even for the 200s: the row asserts status codes,
  //    and a thrown ApiError here would only tell us the test broke, not what the API said.
  const c1 = await raw('GET', `/api/comments/${id1}`, { token: waSession.token });
  const c2 = await raw('GET', `/api/comments/${id2}`, { token: waSession.token });
  const c3 = await raw('GET', `/api/comments/${id3}`, { token: waSession.token });
  expect(c1.status).toBe(200);
  expect(c2.status).toBe(200);
  expect(c3.status).toBe(200);
  expect(c1.data.status).toBe(Status.Applied);
  expect(c1.data.appliedByLabel).toBe('e2e@example.com');
  // origin is a local bare path → unknown host → no link (R2-01-03 flips this to the github cell)
  expect(c1.data.commitUrl).toBeNull();
  expect(c2.data.status).toBe(Status.ReadyToApply);
  expect(c3.data.status).toBe(Status.ReadyToApply);

  // 5. Exactly one new commit with the contract subject; b.txt survives unstaged.
  expect(await countCommits(repo.dir)).toBe(count0 + 1);
  const subject = (await git(repo.dir, ['log', '-1', '--pretty=%s'])).stdout.trim();
  expect(subject).toBe(`Apply Pointer comment #${id1} — Make the CTA primary`);
  expect(await porcelain(repo.dir)).toContain(' M src/b.txt');

  // 6. The bare remote never moved.
  assertRefsUnchanged(bareDir, snapBefore);

  saveEvidence('r2-01-02.txt', `${subject}\n\n${JSON.stringify(c1.data, null, 2)}\n`);
  record({
    id: 'R2-01-02', tier: 'PR', layer: 'cli', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: `one commit "${subject}"; id1 applied with appliedByLabel=e2e@example.com, commitUrl=null; id2/id3 still ready; b.txt untouched`,
  });
});

test('R2-01-05 ⛓ — apply: empty index → exit 1, no PATCH', async () => {
  const start = Date.now();

  // Runs between R2-01-02 and R2-01-03, while id2/id3 are still ReadyToApply.
  const count0 = await countCommits(repo.dir);

  // 1. Empty the index. The worktree stays dirty on purpose: an empty index must fail even when
  //    files are modified — only the STAGED set counts (proves commitAll can't sweep unstaged work).
  await git(repo.dir, ['reset']);

  // 2. --mark <id> with nothing staged → exit 1 naming the id, and no PATCH follows.
  const subId = await runCli(repo.dir, ['apply', '--mark', String(id2), '--reply', 'x']);
  expect(subId.code).toBe(1);
  expect(subId.stderr).toContain(`Nothing staged for #${id2}`);

  // 3. id2 was not touched: still ready, never applied.
  const c2 = await raw('GET', `/api/comments/${id2}`, { token: waSession.token });
  expect(c2.status).toBe(200);
  expect(c2.data.status).toBe(Status.ReadyToApply);
  expect(c2.data.appliedAt).toBeNull();

  // 4. Same for --mark all.
  const subAll = await runCli(repo.dir, ['apply', '--mark', 'all', '--reply', 'x']);
  expect(subAll.code).toBe(1);
  expect(subAll.stderr).toContain('Nothing staged');

  // 5. id3 equally untouched.
  const c3 = await raw('GET', `/api/comments/${id3}`, { token: waSession.token });
  expect(c3.status).toBe(200);
  expect(c3.data.status).toBe(Status.ReadyToApply);
  expect(c3.data.appliedAt).toBeNull();

  // 6. No commit happened, nothing was pushed.
  expect(await countCommits(repo.dir)).toBe(count0);
  assertRefsUnchanged(bareDir, snapBefore);

  record({
    id: 'R2-01-05', tier: 'PR', layer: 'cli', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: 'both --mark forms exited 1 with the staged-index error; id2/id3 still status=2 with appliedAt=null; commit count unchanged',
  });
});

test('R2-01-03 ⛓ — apply: single commit', async () => {
  const start = Date.now();

  // Runs after R2-01-05, so exactly id2 + id3 are still ReadyToApply (id1 went in R2-01-02).
  await patch(`/api/admin/projects/${projectId}`, { commitStyle: 1 }, { token: waSession.token });

  // Re-point origin at a github-shaped URL → commitUrlFor takes the github branch.
  await git(repo.dir, ['remote', 'set-url', 'origin', 'https://github.com/e2e/apply-fixture.git']);

  appendFileSync(join(repo.dir, 'src', 'a.txt'), 'f1\n', 'utf8');
  appendFileSync(join(repo.dir, 'src', 'b.txt'), 'f2\n', 'utf8');
  await git(repo.dir, ['add', '--', 'src/a.txt', 'src/b.txt']);
  const count0 = await countCommits(repo.dir);

  const sub = await runCli(repo.dir, ['apply', '--mark', 'all', '--reply', 'Applied both']);
  expect(sub.code).toBe(0);

  // Exactly one new commit covering BOTH staged files, subject names the pending count.
  expect(await countCommits(repo.dir)).toBe(count0 + 1);
  const newSha = await headSha(repo.dir);
  const subject = (await git(repo.dir, ['log', '-1', '--pretty=%s'])).stdout.trim();
  expect(subject).toBe('Apply 2 pending Pointer comments');

  const c1 = await raw('GET', `/api/comments/${id1}`, { token: waSession.token });
  const c2 = await raw('GET', `/api/comments/${id2}`, { token: waSession.token });
  const c3 = await raw('GET', `/api/comments/${id3}`, { token: waSession.token });
  expect(c1.status).toBe(200);
  expect(c2.status).toBe(200);
  expect(c3.status).toBe(200);

  // id2/id3: applied, both pointing at the same new commit via the github-shaped remote.
  const expectedUrl = `https://github.com/e2e/apply-fixture/commit/${newSha}`;
  expect(c2.data.status).toBe(Status.Applied);
  expect(c3.data.status).toBe(Status.Applied);
  expect(c2.data.commitUrl).toBe(expectedUrl);
  expect(c3.data.commitUrl).toBe(expectedUrl);

  // id1 keeps the null it got under the file-path remote: the CLI never rewrites an applied
  // comment, and only now has the remote changed.
  expect(c1.data.commitUrl).toBeNull();

  assertRefsUnchanged(bareDir, snapBefore);

  saveEvidence('r2-01-03.txt', `commitUrl id1=${c1.data.commitUrl} id2=${c2.data.commitUrl} id3=${c3.data.commitUrl}\n`);
  record({
    id: 'R2-01-03', tier: 'PR', layer: 'cli', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: `one commit "${subject}"; id2/id3 applied with commitUrl=.../commit/${newSha.slice(0, 8)}…; id1 commitUrl still null`,
  });
});

test('R2-01-06 ⛓ — apply: get --json = exact AiCommentView key set', async () => {
  const start = Date.now();

  const sub = await runCli(repo.dir, ['get', String(id1), '--json']);
  expect(sub.code).toBe(0);

  const view = JSON.parse(sub.stdout);

  // Exact whitelist at every level — sorted so the diff names any extra or missing key.
  expect(sortedKeys(view)).toEqual(TOP_LEVEL_KEYS);
  expect(sortedKeys(view.element)).toEqual(ELEMENT_KEYS);

  // id1 carries the reply R2-01-02's --mark left, so replies[0] is real data here.
  expect(Array.isArray(view.replies)).toBe(true);
  expect(view.replies.length).toBeGreaterThan(0);
  expect(sortedKeys(view.replies[0])).toEqual(REPLY_KEYS);

  // The fixture posts no predefined actions, so pickedActions is [] — assert the exact-empty
  // shape and the key set of any item if a future fixture adds one (covers the row's intent).
  expect(Array.isArray(view.pickedActions)).toBe(true);
  if (view.pickedActions.length > 0) {
    expect(sortedKeys(view.pickedActions[0])).toEqual(PICKED_ACTION_KEYS);
  }

  // Untrusted-body envelope (Decision in Preconditions): value + untrusted:true, not a bare string.
  expect(typeof view.body.value).toBe('string');
  expect(view.body.untrusted).toBe(true);
  expect(view.replies[0].body.untrusted).toBe(true);

  // Deep key scan: stringified JSON surfaces every key at every depth; the R2-06 guarantee is
  // that none of these server-side fields survive the projection even though the API returns
  // them (or omits them for headerless callers).
  expect(JSON.stringify(view)).not.toMatch(FORBIDDEN_KEY_RE);

  saveEvidence('r2-01-06-keys.txt', `${JSON.stringify({ top: sortedKeys(view), element: sortedKeys(view.element), reply: sortedKeys(view.replies[0]) }, null, 2)}\n`);
  record({
    id: 'R2-01-06', tier: 'PR', layer: 'cli', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: `top-level keys exactly ${TOP_LEVEL_KEYS.length}; element ${ELEMENT_KEYS.length}; reply ${REPLY_KEYS.length}; no forbidden keys at any depth`,
  });
});

test('R2-01-04 ⛓ — apply: never pushes', async () => {
  const start = Date.now();

  // Runs last (01 → 02 → 05 → 03 → 06 have all committed by now): the bare remote must be
  // byte-identical to the pre-run snapshot. A local tracking ref would prove nothing — which is
  // exactly why the BARE is what gets snapshotted, not the clone.
  const after = refsSnapshot(bareDir);
  expect(after).toBe(snapBefore);

  // And it never received a single object, not even one no ref points at yet.
  const objects = await git(bareDir, ['rev-parse', '--all']);
  expect(objects.stdout.trim()).toBe('');

  record({
    id: 'R2-01-04', tier: 'PR', layer: 'cli', role: 'WA', result: 'PASS', ms: Date.now() - start,
    detail: `bare for-each-ref identical before/after (both empty: "${snapBefore}"); rev-parse --all empty`,
  });
});

test('R2-01-07 ⛓ — apply: non-admin developer falls back to summary view', async () => {
  const start = Date.now();

  if (process.env.TIER === 'pr') {
    record({ id: 'R2-01-07', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'SKIP', ms: 0, detail: 'nightly tier only — skipped during PR tier' });
    test.skip(true, 'nightly tier only — skipped during PR tier');
  }

  const developer = credentials().developer || USERS.developer;
  const devSession = await login(developer.email, developer.password);

  // 1. Ground the trigger: the admin apply-queue refuses a Developer key. raw() — the 403 IS
  //    the assertion; a thrown ApiError in a catch block is exactly the silent-pass trap this
  //    suite exists to prevent.
  const queueRes = await raw('GET', `/api/admin/projects/${projectKey}/apply-queue`, { token: devSession.token });
  expect(queueRes.status).toBe(403);

  // After R2-01-03 the shared queue is empty — re-seed ONE fresh ready comment so the fallback
  // prompt has real content to list, and assert that body shows up.
  const freshBody = 'Nightly fallback check — sharpen the hero contrast';
  const fresh = await post(
    `/api/projects/${projectKey}/comments`,
    { body: freshBody, environment: Environment.Local, element: { selector: '.cta', route: '/', pageUrl: 'http://localhost/x' } },
    { token: testerSession.token },
  );
  await patch(`/api/comments/${fresh.id}`, { status: Status.ReadyToApply }, { token: waSession.token });

  // 2. Its OWN repo (no token bleed from the WA repo's .pointer/) pointed at the same project,
  //    authenticated with the DEVELOPER's seeded key.
  const devRepo = tempRepo();
  try {
    const pointerDir = join(devRepo.dir, '.pointer');
    mkdirSync(pointerDir, { recursive: true });
    writeFileSync(join(pointerDir, 'config.json'), `${JSON.stringify({ server: BASE_URL, project: projectKey }, null, 2)}\n`, 'utf8');
    writeFileSync(join(pointerDir, 'credentials.env'), `POINTER_API_KEY=${keys().developer.apiKey}\n`, 'utf8');

    // 3. Both prompt forms: exit 0, the fallback note exactly once per run (queue.ts's module
    //    guard must reset per process — each spawn is a fresh CLI), the fresh body listed, and
    //    none of the admin-only payload (picked-action prompts, aiRules) leaking through.
    for (const args of [['apply'], ['apply', '--plan']]) {
      const sub = await runCli(devRepo.dir, args);
      expect(sub.code).toBe(0);
      expect(occurrences(sub.stdout, NON_ADMIN_NOTE)).toBe(1);
      expect(sub.stdout).toContain(freshBody);
      expect(sub.stdout).not.toContain('Picked actions');
      expect(sub.stdout).toContain('- None active');
    }

    // 4. list --status ready --json: summary-view items only — exact CommentSummaryDto keys.
    const list = await runCli(devRepo.dir, ['list', '--status', 'ready', '--json']);
    expect(list.code).toBe(0);
    const items = JSON.parse(list.stdout);
    expect(Array.isArray(items)).toBe(true);
    expect(items.some((i) => i.id === fresh.id && i.body === freshBody)).toBe(true);
    for (const item of items) {
      expect(sortedKeys(item)).toEqual(SUMMARY_KEYS);
    }

    saveEvidence('r2-01-07.txt', list.stdout);
    record({
      id: 'R2-01-07', tier: 'nightly', layer: 'cli', role: 'DEV', result: 'PASS', ms: Date.now() - start,
      detail: 'apply-queue 403 for DEV; fallback note once per run; summary-view keys exact; no picked-action or aiRules prompts',
    });
  } finally {
    devRepo.cleanup();
  }
});

test.afterAll(async () => {
  // Spec files: "assertRefsUnchanged re-asserted in after() (R2-01-04)" — assert FIRST, while
  // the bare still exists, so a run that failed midway still answers the never-push question.
  try {
    if (bareDir && repo) assertRefsUnchanged(bareDir, snapBefore);
  } finally {
    // Restore everything this file created: the two temp repos, the bare remote, and the
    // dedicated project (leftover projects would pollute the shared database the next phase
    // runs against).
    try { repo?.cleanup(); } catch { /* already gone */ }
    if (bareDir) rmSync(bareDir, { recursive: true, force: true });
    if (projectId && waSession) {
      await raw('DELETE', `/api/admin/projects/${projectId}`, { token: waSession.token });
    }
  }
});
