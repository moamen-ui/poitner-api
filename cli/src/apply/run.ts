import { promises as fs } from 'node:fs';
import { resolveSource } from '../vite/resolve.js';
import { join, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { api } from '../api.js';
import { postEvent } from '../events.js';
import { fetchQueue, type QueueFilter } from './queue.js';
import { loadProjectContext } from './context.js';
import { buildApplyPrompt } from './prompt.js';
import { readConfig, isMultiProject } from '../config.js';
import { stackFileRelPath } from '../stack/stackfile.js';
import type { ApplyClientContext, ApplyProjectContext, ApplyRunResult, QueueItem } from './types.js';

export type ApplyRunOptions = {
  plan?: boolean;
  tool?: 'claude' | 'opencode' | 'cursor' | 'clipboard' | string;
  status?: number | string;
  environment?: number | string;
};

export function detectAiTool(override?: string): string {
  if (override) {
    if (override === 'claude') return 'claude-code';
    return override;
  }
  if (process.env.POINTER_AI_TOOL) return process.env.POINTER_AI_TOOL;
  if (process.env.CLAUDECODE || process.env.CLAUDE_CODE_ENTRYPOINT) return 'claude-code';
  if (process.env.ANTIGRAVITY_AGENT || process.env.GEMINI_CLI) return 'antigravity';
  if (process.env.TERM_PROGRAM && process.env.TERM_PROGRAM.includes('Cursor')) return 'cursor';
  if (process.env.WINDSURF) return 'windsurf';
  return 'other';
}

export async function ensureToolRegistered(
  ctx: ApplyClientContext,
  tool: string,
): Promise<void> {
  const config = await readConfig(ctx.cwd).catch(() => ({}) as any);
  const relStackPath = isMultiProject(config) ? stackFileRelPath(ctx.project) : stackFileRelPath();
  const stackPath = join(ctx.cwd, relStackPath);
  let stackData: any = {};
  try {
    const raw = await fs.readFile(stackPath, 'utf8');
    stackData = JSON.parse(raw);
  } catch {}

  const aiTools: string[] = Array.isArray(stackData.aiTools) ? stackData.aiTools : [];
  if (aiTools.includes(tool)) return;

  if (ctx.token) {
    try {
      const res = await api<any>(
        ctx.server,
        `/api/projects/${encodeURIComponent(ctx.project)}/stack`,
        {
          method: 'POST',
          body: { aiTool: tool },
          token: ctx.token,
        },
      );
      if (res) {
        await fs.mkdir(dirname(stackPath), { recursive: true });
        await fs.writeFile(stackPath, JSON.stringify(res, null, 2) + '\n', 'utf8');
        return;
      }
    } catch {}
  }

  // Local fallback cache update
  aiTools.push(tool);
  stackData.aiTools = aiTools;
  try {
    await fs.mkdir(dirname(stackPath), { recursive: true });
    await fs.writeFile(stackPath, JSON.stringify(stackData, null, 2) + '\n', 'utf8');
  } catch {}
}

function copyToClipboard(text: string): boolean {
  if (process.platform === 'darwin') {
    const proc = spawnSync('pbcopy', { input: text, encoding: 'utf8' });
    return proc.status === 0;
  }
  if (process.platform === 'win32') {
    const proc = spawnSync('clip.exe', { input: text, encoding: 'utf8' });
    return proc.status === 0;
  }
  // Linux / BSD: try xclip then xsel
  let proc = spawnSync('xclip', ['-selection', 'clipboard'], { input: text, encoding: 'utf8' });
  if (proc.status === 0) return true;
  proc = spawnSync('xsel', ['--clipboard', '--input'], { input: text, encoding: 'utf8' });
  return proc.status === 0;
}

export async function runApply(
  options: ApplyRunOptions,
  ctx: ApplyClientContext,
): Promise<ApplyRunResult> {
  await postEvent(ctx.server, ctx.token, {
    type: 'apply_started',
    projectKey: ctx.project,
  });

  const toolName = detectAiTool(options.tool);
  await ensureToolRegistered(ctx, toolName);

  const context = await loadProjectContext(ctx);
  const filter: QueueFilter = {};
  if (options.status !== undefined) filter.status = options.status;
  if (options.environment !== undefined) filter.environment = options.environment;

  const items = await fetchQueue(ctx, filter);

  // Rebuild the manifest before resolving, when the queue contains stamped hashes and the manifest
  // cannot answer for them.
  //
  // Previously the prompt just told the agent the hash was unresolved and suggested the developer
  // run `pointer map --from-source` — advice delivered to the wrong party, at the wrong moment, by
  // a process that could simply do it. Regeneration is local and offline, so the cost of being
  // wrong is a few hundred milliseconds.
  const hashes = items
    .map((i) => i.element?.sourcePath)
    .filter((p): p is string => typeof p === 'string' && /^[0-9a-f]{8}$/.test(p));
  if (hashes.length > 0 && hashes.some((h) => resolveSource(ctx.cwd, h).kind !== 'manifest')) {
    const { buildManifest } = await import('../commands/map.js');
    await buildManifest(ctx.cwd, { quiet: true }).catch(() => null);
  }

  const prompt = buildApplyPrompt(items, context, {
    plan: options.plan,
    resolveSource: (hash) => resolveSource(ctx.cwd, hash),
  });

  if (options.tool) {
    const tool = options.tool.toLowerCase();
    if (tool === 'claude') {
      const res = spawnSync('claude', ['-p', prompt], {
        cwd: ctx.cwd,
        stdio: 'inherit',
      });
      if (res.error) {
        console.error(`Failed to spawn claude: ${res.error.message}`);
      }
    } else if (tool === 'opencode') {
      const args = ['run'];
      if (process.env.OPENCODE_MODEL) {
        args.push('--model', process.env.OPENCODE_MODEL);
      }
      args.push(prompt);
      const res = spawnSync('opencode', args, {
        cwd: ctx.cwd,
        stdio: 'inherit',
      });
      if (res.error) {
        console.error(`Failed to spawn opencode: ${res.error.message}`);
      }
    } else if (tool === 'cursor') {
      const promptFile = join(ctx.cwd, '.pointer/apply-prompt.md');
      await fs.mkdir(join(ctx.cwd, '.pointer'), { recursive: true });
      await fs.writeFile(promptFile, prompt, 'utf8');
      console.log(`Saved apply prompt to ${promptFile}`);
    } else if (tool === 'clipboard') {
      const copied = copyToClipboard(prompt);
      if (copied) {
        console.log('Copied apply prompt to clipboard.');
      } else {
        process.stdout.write(prompt);
      }
    } else {
      console.error(`Unknown tool: ${options.tool}`);
      process.stdout.write(prompt);
    }
  }

  return { prompt, items, context };
}
