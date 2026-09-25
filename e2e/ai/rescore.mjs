#!/usr/bin/env node
// Offline re-scorer: re-derives TC6's diff-and-transcript-only criteria (buttonChanged/
// workspaceRuleWon/noStall) from a PREVIOUSLY SAVED harness run — a `state/` (or
// /tmp/claude-gate/before-state-shaped) directory containing `scratch/<tool>-<case>/` (the
// agent's working git repo, left as-is after the run) and `transcripts/<tool>-<case>.log` — with
// no live server and no AI tool invocation. Exists to answer "did the harness.mjs/audit.mjs diff
// fix (git diff HEAD -> git diff <root commit>) actually change the verdict on a real recorded
// run", without re-running (and re-paying for) the AI tool.
//
// commentProcessed is intentionally NOT re-derived here: it reads live server state
// (`GET /api/comments/{id}`), and the server the original run scored against is long torn down.
// It's reported separately, from the ORIGINAL run's own report.md, since it isn't diff/transcript
// dependent and the harness.mjs/audit.mjs bug this script validates never touched it.
//
// Usage: node e2e/ai/rescore.mjs <state-dir> [caseId-substring-filter]
// e.g.:  node e2e/ai/rescore.mjs /tmp/claude-gate/before-state tc6
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { computeTc6Criteria } from '../scripts/audit.mjs';

const [, , stateDirArg, filterArg] = process.argv;
if (!stateDirArg) {
  console.error('Usage: node e2e/ai/rescore.mjs <state-dir> [case-id-substring-filter]');
  process.exit(1);
}

const STATE_DIR = stateDirArg;
const SCRATCH_DIR = join(STATE_DIR, 'scratch');
const TRANSCRIPTS_DIR = join(STATE_DIR, 'transcripts');
const REPORT_PATH = join(STATE_DIR, 'report.md');

function readOriginalDetail(caseLabel) {
  // Best-effort: pull this case's own row out of the original run's report.md table, purely to
  // show commentProcessed's ORIGINAL verdict (server-state-only, unaffected by this fix) alongside
  // the re-scored diff/transcript criteria — not used for anything else.
  if (!existsSync(REPORT_PATH)) return null;
  const report = readFileSync(REPORT_PATH, 'utf8');
  const row = report.split('\n').find((l) => l.startsWith('|') && l.includes(`| ${caseLabel} |`));
  if (!row) return null;
  const cells = row.split('|').map((c) => c.trim());
  return cells[4] || null; // | tool | case | status | detail |
}

function findBaselineSha(scratchDir) {
  return execFileSync('git', ['rev-list', '--max-parents=0', 'HEAD'], { cwd: scratchDir, encoding: 'utf8' })
    .trim()
    .split('\n')[0];
}

function diffAgainstBaseline(scratchDir) {
  try {
    const sha = findBaselineSha(scratchDir);
    // --unified=1000 — same reason as harness.mjs/audit.mjs: touchedButtonRule's `/submit-btn/`
    // literal-text check needs the selector line inside the shown diff context.
    return execFileSync('git', ['diff', '--unified=1000', sha], { cwd: scratchDir, encoding: 'utf8' });
  } catch {
    return '';
  }
}

function diffAgainstHead(scratchDir) {
  // The OLD, buggy behaviour (harness.mjs/audit.mjs before this fix) — reproduced here only so
  // this script can show the before/after contrast for the same saved scratch dir.
  try {
    return execFileSync('git', ['diff', 'HEAD'], { cwd: scratchDir, encoding: 'utf8' });
  } catch {
    return '';
  }
}

function readAnswerText(transcriptPath) {
  if (!existsSync(transcriptPath)) return '';
  const transcript = readFileSync(transcriptPath, 'utf8');
  const marker = '\n\nRESPONSE:\n';
  const idx = transcript.indexOf(marker);
  return idx >= 0 ? transcript.slice(idx + marker.length) : transcript;
}

function detailOf(criteria) {
  return Object.entries(criteria).map(([k, v]) => `${k}:${v ? 'PASS' : 'FAIL'}`).join(', ');
}

if (!existsSync(SCRATCH_DIR)) {
  console.error(`No scratch/ directory under ${STATE_DIR} — nothing to re-score.`);
  process.exit(1);
}

const scratchDirs = readdirSync(SCRATCH_DIR, { withFileTypes: true })
  .filter((d) => d.isDirectory() && d.name.includes('-tc6') && (!filterArg || d.name.includes(filterArg)))
  .map((d) => d.name)
  .sort();

if (scratchDirs.length === 0) {
  console.error(`No *-tc6* scratch dirs found under ${SCRATCH_DIR}${filterArg ? ` matching "${filterArg}"` : ''}.`);
  process.exit(1);
}

console.log('================================================================================');
console.log(` OFFLINE RE-SCORE (TC6 diff/transcript criteria only) — ${STATE_DIR}`);
console.log('================================================================================');

const rows = [];
for (const name of scratchDirs) {
  const scratchDir = join(SCRATCH_DIR, name);
  const transcriptPath = join(TRANSCRIPTS_DIR, `${name}.log`);
  const answerText = readAnswerText(transcriptPath);

  const oldDiff = diffAgainstHead(scratchDir);
  const newDiff = diffAgainstBaseline(scratchDir);

  const oldCriteria = computeTc6Criteria({ diff: oldDiff, answerText, comment: null });
  const newCriteria = computeTc6Criteria({ diff: newDiff, answerText, comment: null });

  // caseLabel mirrors how run-cases.mjs labelled this run in the ORIGINAL report.md, e.g.
  // "claude-code-tc6-run-1" -> tool "claude-code", case "tc6-run-1".
  const m = name.match(/^(.+?)-(tc6.*)$/);
  const [tool, caseId] = m ? [m[1], m[2]] : [name, 'tc6'];
  const originalDetail = readOriginalDetail(caseId) || readOriginalDetail(name);

  rows.push({ name, tool, caseId, oldCriteria, newCriteria, originalDetail });
}

for (const r of rows) {
  console.log(`\n--- ${r.name} ---`);
  console.log(`  OLD (git diff HEAD, the bug)      : ${detailOf(r.oldCriteria)}`);
  console.log(`  NEW (git diff <root commit>, fixed): ${detailOf(r.newCriteria)}`);
  if (r.originalDetail) console.log(`  Original run's full detail (incl. server-state commentProcessed): ${r.originalDetail}`);
}

console.log('\n================================================================================');
console.log(' SUMMARY');
console.log('================================================================================');
for (const r of rows) {
  const oldPass = Object.values(r.oldCriteria).every((v) => v === true);
  const newPass = Object.values(r.newCriteria).every((v) => v === true);
  console.log(`${r.name}: old=${oldPass ? 'PASS' : 'FAIL'} -> new=${newPass ? 'PASS' : 'FAIL'} (diff/transcript criteria only; commentProcessed excluded — see per-run detail above)`);
}
