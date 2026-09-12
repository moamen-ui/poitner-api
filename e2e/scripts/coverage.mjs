#!/usr/bin/env node
// Scenario-coverage gate.
//
// The testing docs under docs/roadmap/testing/ are implementer-grade contracts: each one lists its
// scenarios in a `## Scenarios` table (first column = id) and names the spec files that implement
// them under `## Spec files`. Nothing previously checked that those spec files exist.
//
// They mostly did not. The whole suite was one file — widget/widget.spec.ts — while run-e2e.sh
// advertised ten phases and the report showed them all PASS. This script makes the difference
// between "documented" and "implemented" a number that appears in every run, so the gap cannot
// quietly persist behind a green tick again.
//
// It counts a scenario as covered when a TEST TITLE claiming that id exists somewhere under e2e/.
// It used to count spec FILES named by the doc's `## Spec files` section instead, which measured
// the wrong thing in both directions: e2e/mail/tenant-invite-mail.spec.mjs implements every R2-07
// scenario but is not the `mail.spec.mjs` the doc names, so it read as missing; and a doc whose
// named files all exist but contain none of its scenarios read as fully implemented. The file
// check is kept as a secondary line, because a doc naming a file nobody wrote is still a signal.
//
// Exit code is always 0: this reports, it does not fail a run. The phases themselves fail.
import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const docsDir = resolve(e2eRoot, '..', 'docs', 'roadmap', 'testing');

/** Pull the rows of a markdown table that follows the given `## Heading`. */
function sectionLines(md, heading) {
  const lines = md.split('\n');
  const start = lines.findIndex((l) => l.trim() === `## ${heading}`);
  if (start === -1) return [];
  const out = [];
  for (let i = start + 1; i < lines.length; i++) {
    if (lines[i].startsWith('## ')) break;
    out.push(lines[i]);
  }
  return out;
}

/**
 * The doc's scenarios, each tagged with whether it is automatable at all.
 *
 * Some scenarios are deliberately NOT specs: R1-07 drives GitHub Actions rather than the stack
 * ("None under `e2e/`" in its Spec files section), and a handful are tiered **manual** because
 * they need a tool no CI step runs (R3-05-02 is a Lighthouse audit through the devtools MCP).
 * Counting those as missing would permanently cap coverage below 100% and train the reader to
 * ignore the number — the opposite of why it exists.
 */
function scenarios(md) {
  const docIsManual = sectionLines(md, 'Spec files').some((l) => /None under/i.test(l));
  const out = [];
  for (const line of sectionLines(md, 'Scenarios')) {
    if (!line.startsWith('|')) continue;
    const cells = line.split('|');
    const first = cells[1]?.trim() ?? '';
    // Ids look like R1-05-01, sometimes with a letter suffix that splits one row into two tiers
    // (R3-03-06 / R3-03-06b). Without the suffix both rows collapse onto one id and the second
    // scenario silently stops being counted. The header and separator rows never match.
    const m = first.match(/^([A-Z0-9]+-\d{2}-\d{2}[a-z]?)/);
    if (!m) continue;
    const tier = (cells[3] ?? '').replace(/\*/g, '').trim().toLowerCase();
    out.push({ id: m[1], manual: docIsManual || tier.startsWith('manual') });
  }
  return out;
}

function specFiles(md) {
  const files = new Set();
  for (const line of sectionLines(md, 'Spec files')) {
    // Paths are written inside backticks, e.g. `e2e/api/origins.spec.mjs`.
    for (const m of line.matchAll(/`(e2e\/[^`]+\.(?:spec\.[a-z]+|mjs|ts))`/g)) files.add(m[1]);
  }
  return [...files];
}

/**
 * Every scenario id that appears in a test title anywhere under e2e/.
 *
 * run-e2e.sh dispatches a single scenario with `npx playwright test -g <id>`, so a title carrying
 * the id is exactly what makes a scenario runnable — the same string this reads.
 */
const blocked = new Set();

function implementedIds() {
  const found = new Set();
  const skip = new Set(['node_modules', 'state', 'test-results', 'playwright-report', '.git']);
  const walk = (dir) => {
    for (const entry of readdirSync(dir)) {
      if (skip.has(entry)) continue;
      const full = join(dir, entry);
      if (statSync(full).isDirectory()) {
        walk(full);
      } else if (/\.spec\.(mjs|ts|js)$/.test(entry)) {
        const src = readFileSync(full, 'utf8');
        // test('R2-06-04 — …'), test.skip("R1-05-01 …"), test(`R3-01-02 …`) all count.
        for (const m of src.matchAll(/\b(?:test|it)(?:\.(?:skip|fixme|only|fail))?\s*\(\s*[`'"]\s*([A-Z0-9]+-\d{2}-\d{2}[a-z]?)/g)) {
          // A scenario whose body opens with `test.fixme(true, …)` is BLOCKED on a feature that
          // does not exist. It has a test, but that test asserts nothing, so counting it as
          // covered would inflate the number with work still to do — the precise failure this
          // gate was rewritten to stop. Look just past the title for the marker.
          const after = src.slice(m.index, m.index + 600);
          if (/test\.fixme\(\s*true/.test(after)) blocked.add(m[1]);
          else found.add(m[1]);
        }
      }
    }
  };
  walk(e2eRoot);
  return found;
}

const implemented = implementedIds();

const rows = [];
let totalScenarios = 0;
let coveredScenarios = 0;
const missingSpecs = new Set();
const missingScenarios = [];
let totalManual = 0;

for (const name of readdirSync(docsDir).filter((f) => f.endsWith('-tests.md')).sort()) {
  const md = readFileSync(join(docsDir, name), 'utf8');
  const all = scenarios(md);
  if (all.length === 0) continue;
  const manual = all.filter((x) => x.manual);
  const ids = all.filter((x) => !x.manual).map((x) => x.id);
  totalManual += manual.length;
  const specs = specFiles(md);
  // A doc counts as implemented only when every spec file it names is on disk. Partial credit
  // would be a guess: we cannot tell which scenarios a half-written file covers.
  const present = specs.filter((s) => existsSync(resolve(e2eRoot, '..', s)));
  const absent = specs.filter((s) => !existsSync(resolve(e2eRoot, '..', s)));
  absent.forEach((s) => missingSpecs.add(s));
  const done = ids.filter((id) => implemented.has(id));
  const todo = ids.filter((id) => !implemented.has(id));
  todo.forEach((id) => missingScenarios.push(id));
  totalScenarios += ids.length;
  coveredScenarios += done.length;
  rows.push({
    doc: name.replace('-tests.md', ''),
    scenarios: ids.length,
    manual: manual.length,
    done: done.length,
    specs: specs.length,
    present: present.length,
    state:
      ids.length === 0 ? 'MANUAL' : todo.length === 0 ? 'IMPLEMENTED' : done.length === 0 ? 'MISSING' : 'PARTIAL',
  });
}

const pct = totalScenarios === 0 ? 0 : Math.round((coveredScenarios / totalScenarios) * 100);

if (process.argv[2] === '--markdown') {
  const out = [];
  out.push(`## Scenario coverage  ${coveredScenarios}/${totalScenarios} automatable scenarios have a test (${pct}%) — ${totalManual} more are manual by design`);
  out.push('| doc | automatable | with a test | manual | spec files on disk | state |');
  out.push('|---|---|---|---|---|---|');
  for (const r of rows) out.push(`| ${r.doc} | ${r.scenarios} | ${r.done} | ${r.manual} | ${r.present}/${r.specs} | ${r.state} |`);
  if (missingScenarios.length) {
    out.push('');
    out.push(`Scenarios with no test (${missingScenarios.length}): ${missingScenarios.join(', ')}`);
  }
  if (missingSpecs.size) {
    out.push('');
    out.push(`Missing spec files (${missingSpecs.size}):`);
    for (const s of [...missingSpecs].sort()) out.push(`- \`${s}\``);
  }
  console.log(out.join('\n'));
} else {
  const blockedHere = [...blocked].sort();
  console.log(
    `Scenario coverage: ${coveredScenarios}/${totalScenarios} automatable (${pct}%)` +
      ` + ${totalManual} manual by design` +
      (blockedHere.length ? ` + ${blockedHere.length} blocked on unbuilt features` : ''),
  );
  for (const r of rows) {
    const man = r.manual ? `  +${r.manual} manual` : '';
    console.log(`  ${r.state.padEnd(12)} ${r.doc}  ${r.done}/${r.scenarios} scenarios   files=${r.present}/${r.specs}${man}`);
  }
  if (missingScenarios.length) {
    console.log(`\nScenarios with no test (${missingScenarios.length}):`);
    console.log('  ' + missingScenarios.join(' '));
  }
  if (blockedHere.length) {
    console.log(`\nBlocked on features that do not exist (${blockedHere.length}):`);
    console.log('  ' + blockedHere.join(' '));
  }
  if (missingSpecs.size) {
    console.log(`\nMissing spec files (${missingSpecs.size}):`);
    for (const s of [...missingSpecs].sort()) console.log(`  ${s}`);
  }
}
