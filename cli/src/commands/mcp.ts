import { readConfig } from '../config.js';
import { api, ApiError } from '../api.js';
import { resolveToken, readApiKey } from '../auth.js';
import { compareSemver, tooOldMessage } from '../checks.js';
import { BUILD_CLI_VERSION, BUILD_DEFAULT_SERVER } from '../build-constants.js';
import { runMcpServer } from '../mcp/server.js';
import type { McpContext } from '../mcp/tools.js';

export async function mcpCommand(
  cwd: string,
  parsed: Record<string, string | boolean> = {},
): Promise<void> {
  const config = await readConfig(cwd).catch(() => ({} as any));
  const server = (
    (typeof parsed['server'] === 'string' ? parsed['server'] : config.server) ||
    process.env.POINTER_SERVER ||
    BUILD_DEFAULT_SERVER
  ).replace(/\/$/, '');

  const project =
    (typeof parsed['project'] === 'string' ? parsed['project'] : config.project) ||
    process.env.POINTER_PROJECT ||
    '';

  const explicitKey = typeof parsed['key'] === 'string' ? parsed['key'] : undefined;
  const apiKey = explicitKey || (await readApiKey(cwd));

  // Requirement: Fails fast with a single stderr line and exit 3 if no key
  if (!apiKey) {
    console.error('Missing POINTER_API_KEY in .pointer/credentials.env or environment');
    process.exit(3);
  }

  const token = await resolveToken(server, cwd, explicitKey).catch(() => undefined);

  // Check /api/meta for minCliVersion compatibility
  let serverTooOld: string | undefined;
  try {
    const meta = await api<any>(server, '/api/meta');
    const minCli = meta?.minCliVersion || '0.0.0';
    if (compareSemver(BUILD_CLI_VERSION, minCli) < 0) {
      serverTooOld = tooOldMessage(BUILD_CLI_VERSION, minCli);
    }
  } catch {
    // Best-effort check; 404 or network failures don't block startup
  }

  const logFile = typeof parsed['log'] === 'string' ? parsed['log'] : undefined;

  const ctx: McpContext & { serverTooOld?: string } = {
    cwd,
    server,
    project,
    token,
    apiKey,
    serverTooOld,
  };

  await runMcpServer(ctx, logFile);
}
