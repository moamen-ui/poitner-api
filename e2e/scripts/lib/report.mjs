import { readFileSync, appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync } from 'node:child_process';
import { get, login } from './api.mjs';
import { USERS } from './constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', '..', 'state');
const REPORT_PATH = join(STATE_DIR, 'report.md');
const JUNIT_PATH = join(STATE_DIR, 'junit.xml');

if (!existsSync(STATE_DIR)) mkdirSync(STATE_DIR, { recursive: true });

function getGitSha() {
  try {
    return execSync('git rev-parse --short HEAD').toString().trim();
  } catch {
    return 'unknown';
  }
}

export async function init(flags) {
  const ts = new Date().toISOString().replace('T', ' ').substring(0, 19) + ' UTC';
  const sha = getGitSha();
  
  let stackLine = 'api /api/meta {unknown} · mailpit messages=unknown';
  try {
    const dev = await login(USERS.developer.email, USERS.developer.password);
    const meta = await get('/api/meta', { token: dev.token });
    let messages = '0';
    try {
      const mailRes = await fetch('http://localhost:8025/api/v1/messages');
      if (mailRes.ok) {
        const mailData = await mailRes.json();
        messages = mailData.total || mailData.messages?.length || 0;
      }
    } catch {
      messages = 'unavailable';
    }
    stackLine = `api /api/meta ${JSON.stringify(meta)} · mailpit messages=${messages}`;
  } catch (err) {
    stackLine = `api /api/meta {unreachable} · mailpit messages=unreachable`;
  }

  const header = `# E2E report — ${ts} · git ${sha} · flags ${flags}
## Stack   ${stackLine}
## Phases  | phase | result | duration | notes |
|---|---|---|---|
## Scenarios | id | tier | layer | role | result | attempts | ms | detail |
|---|---|---|---|---|---|---|---|---|
## Mail evidence | to | subject | scenario id |
|---|---|---|
## Failures  trace paths · \`docker compose logs api --tail 100\`
`;
  writeFileSync(REPORT_PATH, header, 'utf8');
  writeFileSync(JUNIT_PATH, '<?xml version="1.0" encoding="UTF-8"?>\n<testsuites>\n', 'utf8');
}

export function record({ id, tier, layer, role, result, ms, detail, attempts = 1 }) {
  const row = `| ${id} | ${tier || ''} | ${layer || ''} | ${role || ''} | ${result} | ${attempts} | ${ms || ''} | ${detail || ''} |\n`;
  
  if (existsSync(REPORT_PATH)) {
    const content = readFileSync(REPORT_PATH, 'utf8');
    const insertPos = content.indexOf('## Mail evidence');
    if (insertPos !== -1) {
      const newContent = content.slice(0, insertPos) + row + content.slice(insertPos);
      writeFileSync(REPORT_PATH, newContent, 'utf8');
    } else {
      appendFileSync(REPORT_PATH, row);
    }
  } else {
    appendFileSync(REPORT_PATH, row);
  }
  
  // Minimal JUnit addition
  if (!existsSync(JUNIT_PATH)) {
    writeFileSync(JUNIT_PATH, '<?xml version="1.0" encoding="UTF-8"?>\n<testsuites>\n', 'utf8');
  }
  const testcase = `  <testcase name="${id}" classname="${tier}.${layer}" time="${(ms || 0) / 1000}">\n` +
                   (result.includes('FAIL') ? `    <failure message="${detail || 'Failed'}"/>\n` : '') +
                   (result === 'SKIP' ? `    <skipped/>\n` : '') +
                   `  </testcase>\n`;
  appendFileSync(JUNIT_PATH, testcase);
}

export function phase({ name, result, duration, notes = '' }) {
  const row = `| ${name} | ${result} | ${duration || ''} | ${notes} |\n`;
  if (existsSync(REPORT_PATH)) {
    const content = readFileSync(REPORT_PATH, 'utf8');
    const insertPos = content.indexOf('## Scenarios');
    if (insertPos !== -1) {
      const newContent = content.slice(0, insertPos) + row + content.slice(insertPos);
      writeFileSync(REPORT_PATH, newContent, 'utf8');
    } else {
      appendFileSync(REPORT_PATH, row);
    }
  } else {
    appendFileSync(REPORT_PATH, row);
  }
}

export function closeJunit() {
  appendFileSync(JUNIT_PATH, '</testsuites>\n');
}

const args = process.argv.slice(2);
if (args[0] === 'init') {
  init(args.slice(1).join(' ')).catch(console.error);
} else if (args[0] === 'phase') {
  phase({ name: args[1], result: args[2], duration: args[3], notes: args[4] });
} else if (args[0] === 'record') {
  // Usage: record id tier layer role result ms attempts detail
  record({
    id: args[1], tier: args[2], layer: args[3], role: args[4],
    result: args[5], ms: args[6], attempts: args[7], detail: args.slice(8).join(' ')
  });
}
