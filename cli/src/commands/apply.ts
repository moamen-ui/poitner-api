import { readConfig } from '../config.js';
import { api, ApiError } from '../api.js';
import { compareSemver } from '../checks.js';
import { resolveToken, readApiKey } from '../auth.js';
import { BUILD_CLI_VERSION, BUILD_DEFAULT_SERVER } from '../build-constants.js';
import { runApply } from '../apply/run.js';
import { markApplied, markFailed } from '../apply/mark.js';
import { fetchQueue } from '../apply/queue.js';
import { toAiCommentView } from '../apply/projection.js';
import type { ApplyClientContext } from '../apply/types.js';

export async function applyCommand(
  cwd: string,
  parsed: Record<string, string | boolean>,
  positionals: string[] = [],
): Promise<void> {
  const config = await readConfig(cwd);
  const server = (
    (typeof parsed['server'] === 'string' ? parsed['server'] : config.server) ||
    BUILD_DEFAULT_SERVER
  ).replace(/\/$/, '');

  const project =
    (typeof parsed['project'] === 'string' ? parsed['project'] : config.project) || '';

  if (!server) {
    console.error('No server configured. Run `pointer init` or pass --server.');
    process.exit(2);
  }

  if (!project) {
    console.error('No project configured. Run `pointer init` or pass --project.');
    process.exit(2);
  }

  // Check /api/meta for minCliVersion compatibility
  try {
    const meta = await api<any>(server, '/api/meta');
    const minCli = meta?.minCliVersion || '0.0.0';
    if (compareSemver(BUILD_CLI_VERSION, minCli) < 0) {
      console.error(`CLI ${BUILD_CLI_VERSION} is older than the server requires (${minCli})`);
      process.exit(5);
    }
  } catch (err: any) {
    // 404 means old server without /api/meta; other network errors handled in subsequent calls
    if (!(err instanceof ApiError && err.code === 404)) {
      // Best-effort check; continue if /api/meta fails for transient reasons
    }
  }

  const explicitKey = typeof parsed['key'] === 'string' ? parsed['key'] : undefined;
  const token = await resolveToken(server, cwd, explicitKey);
  const apiKey = explicitKey || (await readApiKey(cwd));

  if (!token && !apiKey) {
    console.error('Missing POINTER_API_KEY in .pointer/credentials.env or environment');
    process.exit(3);
  }

  const clientCtx: ApplyClientContext = {
    server,
    project,
    token,
    apiKey,
    cwd,
  };

  // 1. Handling --mark <id>|all
  if (parsed['mark'] !== undefined) {
    const markVal = parsed['mark'];
    let markId: number | 'all';
    if (markVal === true) {
      console.error('--mark requires an ID or "all"');
      process.exit(2);
    } else if (String(markVal).toLowerCase() === 'all') {
      markId = 'all';
    } else {
      const parsedNum = parseInt(String(markVal), 10);
      if (isNaN(parsedNum)) {
        console.error(`Invalid --mark argument: ${markVal}. Expected an integer or "all".`);
        process.exit(2);
      }
      markId = parsedNum;
    }

    const reply = typeof parsed['reply'] === 'string' ? parsed['reply'] : '';
    if (!reply) {
      console.error('--reply "<text>" is required when using --mark');
      process.exit(2);
    }

    const noCommit = parsed['no-commit'] === true;
    const dryRun = parsed['dry-run'] === true;
    const tool = typeof parsed['tool'] === 'string' ? parsed['tool'] : undefined;

    await markApplied(
      {
        id: markId,
        reply,
        noCommit,
        dryRun,
        tool,
      },
      clientCtx,
    );
    process.exit(0);
  }

  // 2. Handling --fail <id>
  if (parsed['fail'] !== undefined) {
    const failVal = parsed['fail'];
    if (failVal === true) {
      console.error('--fail requires a comment ID');
      process.exit(2);
    }
    const failId = parseInt(String(failVal), 10);
    if (isNaN(failId)) {
      console.error(`Invalid --fail argument: ${failVal}. Expected integer ID.`);
      process.exit(2);
    }

    const reason = typeof parsed['reason'] === 'string' ? parsed['reason'] : '';
    if (!reason) {
      console.error('--reason "<text>" is required when using --fail');
      process.exit(2);
    }

    await markFailed(failId, reason, clientCtx);
    process.exit(0);
  }

  // 3. Handling query with --json
  if (parsed['json'] === true && !parsed['plan'] && !parsed['tool']) {
    const items = await fetchQueue(clientCtx, {
      status: typeof parsed['status'] === 'string' ? parsed['status'] : undefined,
      environment: typeof parsed['env'] === 'string' ? parsed['env'] : undefined,
    });
    const projections = items.map((item) => toAiCommentView(item));
    console.log(JSON.stringify(projections, null, 2));
    process.exit(0);
  }

  // 4. Default apply run
  const plan = parsed['plan'] === true;
  const tool = typeof parsed['tool'] === 'string' ? parsed['tool'] : undefined;
  const status = typeof parsed['status'] === 'string' ? parsed['status'] : undefined;
  const environment = typeof parsed['env'] === 'string' ? parsed['env'] : undefined;

  const result = await runApply(
    {
      plan,
      tool,
      status,
      environment,
    },
    clientCtx,
  );

  if (!tool) {
    process.stdout.write(result.prompt);
  }

  process.exit(0);
}
