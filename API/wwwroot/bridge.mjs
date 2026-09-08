#!/usr/bin/env node
// Pointer local apply-bridge — installed by install.sh into a host repo's .pointer/bridge.mjs,
// started via `./.pointer/pointer.sh serve`. Lets the widget (running on localhost only) trigger
// a DEVELOPER'S OWN already-installed/authenticated AI CLI tool to apply pending Pointer comments,
// instead of the developer opening a terminal, cd-ing in, and typing the apply prompt by hand.
//
// Deliberately does NOT call any AI provider API directly — it only spawns a local CLI binary the
// developer already has (Claude Code, Antigravity's `agy`, opencode/GLM, …), so there is no extra
// per-call cost beyond what that tool already costs the developer.
//
// Security model (this is a prototype — see the branch's PR description for known follow-ups):
//   - Binds to 127.0.0.1 ONLY — never reachable from another machine.
//   - Validates the request's Origin header against an allowlist (any http://localhost:*  or
//     http://127.0.0.1:* origin by default, since Origin can't be forged by page JS — only another
//     LOCAL page could spoof a matching origin, and only if it happened to run on the same host/
//     port scheme). This blocks the classic "malicious remote site drive-by's your local dev
//     server" class of attack; it does NOT yet add a shared-secret token, which would close the
//     narrower "another local tab on a matching-looking origin" gap — deferred, see README notes.
// No third-party dependencies — Node's built-in `http` only, so nothing to `npm install`.

import http from 'node:http';
import { spawn, spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.dirname(SCRIPT_DIR); // .pointer/bridge.mjs -> repo root
const PORT = Number(process.argv[2]) || 4772;

// name (matches skill.md/pointer.sh's own detect_ai_tool() vocabulary) -> how to check it's
// installed and how to invoke it non-interactively for a fixed apply prompt.
const TOOLS = {
  'claude-code': {
    bin: 'claude',
    // --output-format stream-json emits one JSON object per line (assistant messages, tool_use/
    // tool_result blocks, a final {"type":"result",...}) instead of plain text — confirmed live
    // against a real run. parseStreamLine below turns that into a precise, live "current stage"
    // (which tool is running, on which file) instead of guessing from a fixed prompt instruction.
    buildArgs: (prompt) => ['-p', prompt, '--dangerously-skip-permissions', '--output-format', 'stream-json', '--verbose'],
    parseStreamLine: parseClaudeStreamLine,
  },
  antigravity: {
    bin: 'agy',
    // NOT --mode accept-edits: that only auto-approves file edits, not shell commands — but
    // applying needs to run pointer.sh/curl to fetch the queue and mark comments applied. With
    // no human present to approve a command prompt, agy auto-denies it, does nothing, and still
    // exits 0 (confirmed live: a real run produced "no output produced — a tool required the
    // 'command' permission that headless mode cannot prompt for... re-run with
    // --dangerously-skip-permissions"). This bridge is headless by design, so skip permissions
    // outright, same as claude-code's own flag above.
    buildArgs: (prompt) => ['-p', prompt, '--dangerously-skip-permissions', '--print-timeout', '20m'],
  },
  'opencode-glm': {
    bin: 'opencode',
    buildArgs: (prompt, cwd) => ['run', '-m', 'zai-coding-plan/glm-5.2', '--dir', cwd, prompt],
  },
};

// Turns one line of claude-code's --output-format stream-json into a short, human-readable
// "what's happening right now" label, stored on job.stage. Real schema confirmed live: each line
// is a JSON object with a `type` — "assistant" messages carry a `content` array of `text` and/or
// `tool_use` blocks; the terminal line is {"type":"result", "result": "<final text>", ...}.
function parseClaudeStreamLine(line, job) {
  let evt;
  try { evt = JSON.parse(line); } catch { return; }
  if (evt.type === 'assistant' && Array.isArray(evt.message?.content)) {
    for (const item of evt.message.content) {
      if (item.type === 'tool_use') job.stage = describeToolUse(item.name, item.input);
      else if (item.type === 'text' && item.text?.trim()) job.stage = item.text.trim().slice(0, 100);
    }
  } else if (evt.type === 'result' && typeof evt.result === 'string') {
    job.output = evt.result;
  }
}

function describeToolUse(name, input) {
  if (name === 'Bash') {
    const cmd = String(input?.command || '');
    if (/login-with-key/.test(cmd)) return 'Logging in / registering the tool';
    if (/apply-queue/.test(cmd)) return 'Fetching the apply queue and AI rules';
    if (/\/comments(\?|$)/.test(cmd) && !/PATCH|-X\s*PATCH/i.test(cmd)) return 'Fetching pending comments';
    if (/-X\s*PATCH/i.test(cmd) || /"status":\s*3/.test(cmd)) return 'Marking a comment applied';
    return 'Running: ' + cmd.replace(/\s+/g, ' ').slice(0, 70);
  }
  if (name === 'Edit' || name === 'Write') return `Editing ${input?.file_path ? input.file_path.split('/').pop() : 'a file'}`;
  if (name === 'Read') return `Locating ${input?.file_path ? input.file_path.split('/').pop() : 'the element'}`;
  if (name === 'Grep' || name === 'Glob') return 'Searching the codebase';
  return `Running ${name}`;
}

// Asks the agent to echo a plain-text progress marker before each major step — a fallback for
// tools OTHER than claude-code (antigravity, opencode/GLM), which don't support the structured
// stream-json parsing above. Tool-agnostic (every CLI can already run a shell command as part of
// normal operation) — the bridge captures their raw stdout/stderr into job.output verbatim, so
// these markers show up there for free; the widget picks out the last "[STAGE] ..." line.
const APPLY_PROMPT =
  "Follow this repo's pointer-feedback skill (skill.md, installed under .claude/skills/pointer-feedback " +
  'or .agents/pointer-feedback) and apply all pending Pointer comments now. ' +
  'As you go, before each major step run a plain shell command that echoes a short progress marker ' +
  'on its own line, in exactly this format: [STAGE] <present-tense description, under 8 words>. Do ' +
  'this before: registering/logging in the tool, fetching the pending comments, fetching the apply-' +
  'queue/AI rules, and for EACH comment you apply — before locating its element, before making the ' +
  'edit, and before marking it applied (name the comment id in that last one, e.g. ' +
  '"[STAGE] Marking comment #42 applied"). These are shown live to a developer watching a progress ' +
  'indicator in a browser widget, so keep them short and skip markers for trivial/skipped comments.';

function isInstalled(bin) {
  // `command -v` via a real shell so PATH/aliases resolve the same way a developer's own shell
  // would — spawnSync avoids the async-callback plumbing for what's a cheap, one-shot check.
  const res = spawnSync('/bin/sh', ['-lc', `command -v ${bin}`], { encoding: 'utf8' });
  return res.status === 0 && res.stdout.trim().length > 0;
}

function isAllowedOrigin(origin) {
  if (!origin) return false;
  try {
    const u = new URL(origin);
    return (u.hostname === 'localhost' || u.hostname === '127.0.0.1') && u.protocol === 'http:';
  } catch {
    return false;
  }
}

function withCors(req, res) {
  const origin = req.headers.origin;
  if (isAllowedOrigin(origin)) {
    res.setHeader('Access-Control-Allow-Origin', origin);
    res.setHeader('Vary', 'Origin');
    res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  }
}

function json(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

// In-memory only — this process's own lifetime is the job's lifetime; a restart loses history,
// which is fine (nothing here needs to survive a restart).
const jobs = new Map();
let nextJobId = 1;
// The one job currently in flight, if any — the bridge (not the browser tab) is the source of
// truth for "is something running right now", since a page refresh wipes the widget's own JS
// state but must NOT let the developer accidentally start a second, overlapping apply run.
let currentJobId = null;

function startApplyJob(tool) {
  const def = TOOLS[tool];
  const id = String(nextJobId++);
  const job = { id, tool, status: 'running', output: '', stage: null, exitCode: null };
  jobs.set(id, job);
  currentJobId = id;

  const child = spawn(def.bin, def.buildArgs(APPLY_PROMPT, PROJECT_ROOT), {
    cwd: PROJECT_ROOT,
    env: process.env,
  });
  if (def.parseStreamLine) {
    // NDJSON can arrive split across 'data' events at any byte offset — buffer and only parse
    // complete lines, carrying a trailing partial line over to the next chunk.
    let buffer = '';
    child.stdout.on('data', (d) => {
      buffer += d.toString();
      const lines = buffer.split('\n');
      buffer = lines.pop();
      for (const line of lines) if (line.trim()) def.parseStreamLine(line, job);
    });
  } else {
    child.stdout.on('data', (d) => { job.output += d.toString(); });
  }
  child.stderr.on('data', (d) => { job.output += d.toString(); });
  child.on('close', (code) => {
    job.exitCode = code;
    job.status = code === 0 ? 'done' : 'error';
    if (currentJobId === id) currentJobId = null;
  });
  child.on('error', (err) => {
    job.status = 'error';
    job.output += `\n[bridge] failed to start ${def.bin}: ${err.message}`;
    if (currentJobId === id) currentJobId = null;
  });

  return job;
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => { data += c; });
    req.on('end', () => resolve(data));
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  withCors(req, res);

  if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }

  // Every non-preflight request must come from an allowed local origin — this is the bridge's
  // primary defense (see the file header's security-model note).
  if (!isAllowedOrigin(req.headers.origin)) { json(res, 403, { error: 'origin not allowed' }); return; }

  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);

  if (req.method === 'GET' && url.pathname === '/health') {
    json(res, 200, { ok: true });
    return;
  }

  if (req.method === 'GET' && url.pathname === '/tools') {
    const available = Object.entries(TOOLS)
      .filter(([, def]) => isInstalled(def.bin))
      .map(([name]) => name);
    json(res, 200, { tools: available });
    return;
  }

  if (req.method === 'POST' && url.pathname === '/apply') {
    if (currentJobId) { json(res, 409, { error: 'a run is already in progress', jobId: currentJobId }); return; }
    let body;
    try { body = JSON.parse((await readBody(req)) || '{}'); } catch { body = {}; }
    const tool = body.tool;
    if (!tool || !TOOLS[tool]) { json(res, 400, { error: 'unknown or missing tool' }); return; }
    if (!isInstalled(TOOLS[tool].bin)) { json(res, 400, { error: `${tool} is not installed on this machine` }); return; }
    const job = startApplyJob(tool);
    json(res, 202, { jobId: job.id });
    return;
  }

  // Lets the widget recover "something is already running" after a page refresh, since the
  // bridge — not the browser tab's own JS state — is the source of truth for that.
  if (req.method === 'GET' && url.pathname === '/apply/current') {
    if (!currentJobId) { json(res, 200, { jobId: null }); return; }
    const job = jobs.get(currentJobId);
    json(res, 200, { jobId: job.id, tool: job.tool, status: job.status, stage: job.stage, output: job.output, exitCode: job.exitCode });
    return;
  }

  const statusMatch = req.method === 'GET' && url.pathname.match(/^\/apply\/([^/]+)\/status$/);
  if (statusMatch) {
    const job = jobs.get(statusMatch[1]);
    if (!job) { json(res, 404, { error: 'unknown job' }); return; }
    json(res, 200, { status: job.status, stage: job.stage, output: job.output, exitCode: job.exitCode });
    return;
  }

  json(res, 404, { error: 'not found' });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Pointer local apply-bridge listening on http://127.0.0.1:${PORT} (project root: ${PROJECT_ROOT})`);
  console.log('Bound to 127.0.0.1 only — not reachable from other machines.');
});
