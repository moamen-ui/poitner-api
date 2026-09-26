// Unit-level tests for e2e/scripts/audit.mjs's pure scoring logic — NO server, NO Docker, NO AI
// tool. Run with `node --test e2e/scripts/audit.unit.test.mjs` (or `node --test e2e/scripts` to
// pick up every *.test.mjs). Exists to pin down, with cheap synthetic + recorded-transcript
// fixtures, the three bugs found auditing the Layer-B (real-agent) harness's scoring of a
// completed run (see git history around this file for the full writeup):
//
//   1. `wasTouchedByAgent()` must NOT treat a comment seed.mjs pre-applies itself
//      (`appliedByLabel: 'seed.mjs (pre-applied)'`, e.g. TC3/TC4's C8) as agent-touched — the bug
//      this replaced (`!byId[id]`) FAILed TC3's noHallucinatedTouch on literally every run because
//      C8 is always status=3 regardless of what the agent does.
//   2. `computeTc6Criteria()`'s buttonChanged/workspaceRuleWon/noStall must be computed from a diff
//      against a FIXED baseline commit, with enough context (`--unified=1000`) that the touched
//      selector line is actually present in the diff text — the bug this replaced
//      (`git diff HEAD`, default 3-line context) silently zeroed these for every TC6 run in
//      /tmp/claude-gate/before-state where the agent correctly committed its change.
//   3. audit.mjs's CLI dispatcher must not fire merely because the module was IMPORTED (e.g. by
//      e2e/ai/rescore.mjs, which takes its own positional args on the same process.argv).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { computeTc6Criteria, wasTouchedByAgent, SEED_PRE_APPLIED_LABEL } from './audit.mjs';

// --- wasTouchedByAgent (TC3 noHallucinatedTouch / TC4 excludedIdsUntouched) ---------------------

test('wasTouchedByAgent: seed.mjs pre-applied item (C8-shaped) is NOT counted as agent-touched', () => {
  const c8 = { id: 8, status: 3, appliedByLabel: SEED_PRE_APPLIED_LABEL };
  assert.equal(wasTouchedByAgent(c8), false);
});

test('wasTouchedByAgent: an item genuinely applied by the CLI (different appliedByLabel) IS touched', () => {
  const genuinelyApplied = { id: 2, status: 3, appliedByLabel: 'pointer-feedback CLI' };
  assert.equal(wasTouchedByAgent(genuinelyApplied), true);
});

test('wasTouchedByAgent: an item absent from the status=3 set (never applied) is NOT touched', () => {
  assert.equal(wasTouchedByAgent(undefined), false);
});

test('wasTouchedByAgent: status !== 3 (e.g. ReadyToApply) is NOT touched even with a label present', () => {
  assert.equal(wasTouchedByAgent({ id: 5, status: 2, appliedByLabel: 'whatever' }), false);
});

// --- computeTc6Criteria (buttonChanged / workspaceRuleWon / commentProcessed / noStall) ---------

const SUBMIT_BTN_DIFF_WITH_TOKEN = `diff --git a/style.css b/style.css
index f6c5b26..897bcbc 100644
--- a/style.css
+++ b/style.css
@@ -14,6 +14,8 @@
 .submit-btn {
   padding: 0.5rem 1.25rem;
   border: 1px solid #999999;
   font-size: 1rem;
   border-radius: 4px;
+  background-color: var(--brand);
+  color: var(--brand-contrast);
 }
`;

const SUBMIT_BTN_DIFF_WITH_HEX = `diff --git a/style.css b/style.css
index f6c5b26..1111111 100644
--- a/style.css
+++ b/style.css
@@ -14,6 +14,7 @@
 .submit-btn {
   padding: 0.5rem 1.25rem;
   border: 1px solid #999999;
   font-size: 1rem;
   border-radius: 4px;
+  background-color: #ff00aa;
 }
`;

test('computeTc6Criteria: project-tier rule (var(--brand)) wins, no stall — full PASS', () => {
  const { criteria } = { criteria: computeTc6Criteria({ diff: SUBMIT_BTN_DIFF_WITH_TOKEN, answerText: 'Applied ✓ — done.' }) };
  assert.equal(criteria.buttonChanged, true);
  assert.equal(criteria.workspaceRuleWon, true);
  assert.equal(criteria.noStall, true);
});

test('computeTc6Criteria: personal rule (hard-coded hex) winning fails workspaceRuleWon', () => {
  const criteria = computeTc6Criteria({ diff: SUBMIT_BTN_DIFF_WITH_HEX, answerText: 'Applied ✓ — done.' });
  assert.equal(criteria.buttonChanged, true);
  assert.equal(criteria.workspaceRuleWon, false, 'a newly-added hex literal must fail this even though the edit is otherwise real');
});

test('computeTc6Criteria: an answer ending in a question is a stall', () => {
  const criteria = computeTc6Criteria({
    diff: SUBMIT_BTN_DIFF_WITH_TOKEN,
    answerText: 'I read both rules. Should I proceed with the project-tier one?',
  });
  assert.equal(criteria.noStall, false);
});

test('computeTc6Criteria: an unchecked verification checklist item in the tail is a stall', () => {
  const criteria = computeTc6Criteria({
    diff: SUBMIT_BTN_DIFF_WITH_TOKEN,
    answerText: 'Verification:\n- [x] Read workspace rules\n- [ ] Confirm personal rule disregarded\nDone.',
  });
  assert.equal(criteria.noStall, false);
});

test('computeTc6Criteria: no edit at all fails buttonChanged and noStall regardless of prose', () => {
  const criteria = computeTc6Criteria({ diff: '', answerText: 'Applied ✓ — done.' });
  assert.equal(criteria.buttonChanged, false);
  assert.equal(criteria.noStall, false);
});

test('computeTc6Criteria: commentProcessed reads a markFailed "Could not apply:" reply as processed', () => {
  const criteria = computeTc6Criteria({
    diff: SUBMIT_BTN_DIFF_WITH_TOKEN,
    answerText: 'Applied ✓',
    comment: { status: 2, replies: [{ body: 'Could not apply: ambiguous selector' }] },
  });
  assert.equal(criteria.commentProcessed, true);
});

test('computeTc6Criteria: commentProcessed is false with no status=3 and no markFailed reply', () => {
  const criteria = computeTc6Criteria({ diff: SUBMIT_BTN_DIFF_WITH_TOKEN, answerText: 'Applied ✓', comment: { status: 2, replies: [] } });
  assert.equal(criteria.commentProcessed, false);
});

// --- Regression test for the real `git diff` bug (HEAD vs. a fixed baseline; default vs. wide
// context) — a real temp git repo, no server/Docker, mirrors harness.mjs's runCase() exactly. -----

function sh(cmd, args, cwd) {
  return execFileSync(cmd, args, { cwd, encoding: 'utf8' });
}

test('git diff regression: HEAD zeroes the diff once the agent commits; a fixed baseline SHA does not', () => {
  const dir = mkdtempSync(join(tmpdir(), 'audit-unit-git-'));
  try {
    sh('git', ['init', '-q'], dir);
    sh('git', ['-c', 'user.email=t@example.test', '-c', 'user.name=t', 'commit', '--allow-empty', '-q', '-m', 'baseline'], dir);
    const baselineSha = sh('git', ['rev-parse', 'HEAD'], dir).trim();

    // Mirror the agent: edit a file, then COMMIT it (skill.md's documented convention).
    writeFileSync(join(dir, 'style.css'), '.submit-btn { background-color: var(--brand); }\n');
    sh('git', ['add', '-A'], dir);
    sh('git', ['-c', 'user.email=t@example.test', '-c', 'user.name=t', 'commit', '-q', '-m', 'Apply 1 pending Pointer comments'], dir);

    const diffAgainstHead = sh('git', ['diff', 'HEAD'], dir);
    assert.equal(diffAgainstHead.trim(), '', 'reproduces the bug: HEAD now IS the agent\'s own commit, so this is always empty');

    const diffAgainstBaseline = sh('git', ['diff', '--unified=1000', baselineSha], dir);
    assert.match(diffAgainstBaseline, /submit-btn/, 'the fix: diffing against the fixed baseline SHA (with wide context) surfaces the real change');

    const criteriaFromBrokenDiff = computeTc6Criteria({ diff: diffAgainstHead, answerText: 'Applied ✓' });
    const criteriaFromFixedDiff = computeTc6Criteria({ diff: diffAgainstBaseline, answerText: 'Applied ✓' });
    assert.equal(criteriaFromBrokenDiff.buttonChanged, false);
    assert.equal(criteriaFromFixedDiff.buttonChanged, true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('git diff regression: default 3-line context can miss the selector line; --unified=1000 does not', () => {
  // Exact shape of e2e/fixture-app/tc6/style.css (body {...}, blank line, .submit-btn {...}) —
  // reproduces the real failure seen in
  // /tmp/claude-gate/before-state/scratch/opencode-glm-tc6-run-1: the edit lands on the LAST two
  // lines of `.submit-btn { ... }`, which puts the selector line 4 lines back — one line outside
  // git's default 3-line context window — so neither the shown context lines nor git's own
  // funcname heuristic (which picks the WRONG preceding block, "body {", for its hunk header) ever
  // put the literal substring "submit-btn" anywhere in the diff text.
  const dir = mkdtempSync(join(tmpdir(), 'audit-unit-git-context-'));
  try {
    sh('git', ['init', '-q'], dir);
    const baseline = [
      'body {',
      '  font-family: sans-serif;',
      '  margin: 0;',
      '  padding: 2rem;',
      '}',
      '',
      '.submit-btn {',
      '  padding: 0.5rem 1.25rem;',
      '  border: 1px solid #999999;',
      '  font-size: 1rem;',
      '  border-radius: 4px;',
      '}',
      '',
    ];
    writeFileSync(join(dir, 'style.css'), baseline.join('\n'));
    sh('git', ['add', '-A'], dir);
    sh('git', ['-c', 'user.email=t@example.test', '-c', 'user.name=t', 'commit', '-q', '-m', 'baseline'], dir);

    const edited = baseline.slice(0, -2).concat([
      '  background-color: var(--brand);',
      '  color: var(--brand-contrast);',
      '}',
      '',
    ]);
    writeFileSync(join(dir, 'style.css'), edited.join('\n'));

    const narrowDiff = sh('git', ['diff'], dir); // default context
    const wideDiff = sh('git', ['diff', '--unified=1000'], dir);

    assert.doesNotMatch(narrowDiff, /submit-btn/, 'default context reproduces the bug: the selector line is not in the diff text at all');
    assert.match(wideDiff, /submit-btn/, '--unified=1000 (the fix) always includes it');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
