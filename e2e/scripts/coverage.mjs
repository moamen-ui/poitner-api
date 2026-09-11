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
// Exit code is always 0: this reports, it does not fail a run. The phases themselves fail.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
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

function scenarioIds(md) {
  const ids = [];
  for (const line of sectionLines(md, 'Scenarios')) {
    if (!line.startsWith('|')) continue;
    const first = line.split('|')[1]?.trim() ?? '';
    // Ids look like R1-05-01; the table's header row and separator row never match.
    const m = first.match(/^([A-Z0-9]+-\d{2}-\d{2})/);
    if (m) ids.push(m[1]);
  }
  return ids;
}

function specFiles(md) {
  const files = new Set();
  for (const line of sectionLines(md, 'Spec files')) {
    // Paths are written inside backticks, e.g. `e2e/api/origins.spec.mjs`.
    for (const m of line.matchAll(/`(e2e\/[^`]+\.(?:spec\.[a-z]+|mjs|ts))`/g)) files.add(m[1]);
  }
  return [...files];
}

const rows = [];
let totalScenarios = 0;
let coveredScenarios = 0;
const missingSpecs = new Set();

for (const name of readdirSync(docsDir).filter((f) => f.endsWith('-tests.md')).sort()) {
  const md = readFileSync(join(docsDir, name), 'utf8');
  const ids = scenarioIds(md);
  if (ids.length === 0) continue;
  const specs = specFiles(md);
  // A doc counts as implemented only when every spec file it names is on disk. Partial credit
  // would be a guess: we cannot tell which scenarios a half-written file covers.
  const present = specs.filter((s) => existsSync(resolve(e2eRoot, '..', s)));
  const absent = specs.filter((s) => !existsSync(resolve(e2eRoot, '..', s)));
  absent.forEach((s) => missingSpecs.add(s));
  totalScenarios += ids.length;
  if (absent.length === 0 && specs.length > 0) coveredScenarios += ids.length;
  rows.push({
    doc: name.replace('-tests.md', ''),
    scenarios: ids.length,
    specs: specs.length,
    present: present.length,
    state: specs.length === 0 ? 'NO-SPECS-NAMED' : absent.length === 0 ? 'IMPLEMENTED' : 'MISSING',
  });
}

const pct = totalScenarios === 0 ? 0 : Math.round((coveredScenarios / totalScenarios) * 100);

if (process.argv[2] === '--markdown') {
  const out = [];
  out.push(`## Scenario coverage  ${coveredScenarios}/${totalScenarios} documented scenarios have all their spec files on disk (${pct}%)`);
  out.push('| doc | scenarios | spec files | on disk | state |');
  out.push('|---|---|---|---|---|');
  for (const r of rows) out.push(`| ${r.doc} | ${r.scenarios} | ${r.specs} | ${r.present} | ${r.state} |`);
  if (missingSpecs.size) {
    out.push('');
    out.push(`Missing spec files (${missingSpecs.size}):`);
    for (const s of [...missingSpecs].sort()) out.push(`- \`${s}\``);
  }
  console.log(out.join('\n'));
} else {
  console.log(`Scenario coverage: ${coveredScenarios}/${totalScenarios} (${pct}%)`);
  for (const r of rows) {
    console.log(`  ${r.state.padEnd(15)} ${r.doc}  scenarios=${r.scenarios} specs=${r.present}/${r.specs}`);
  }
  if (missingSpecs.size) {
    console.log(`\nMissing spec files (${missingSpecs.size}):`);
    for (const s of [...missingSpecs].sort()) console.log(`  ${s}`);
  }
}
