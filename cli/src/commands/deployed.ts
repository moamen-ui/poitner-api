import { execFileSync } from 'node:child_process';
import { api } from '../api.js';
import { getClient } from './comments.js';

/**
 * Is `ancestor` contained in `sha`?
 *
 * This is the question the server cannot answer — it has no clone. Answering it where the
 * repository actually is, and sending the server a plain list of shas, keeps the server ignorant
 * of git entirely.
 */
function contains(cwd: string, ancestor: string, sha: string): boolean {
  try {
    execFileSync('git', ['merge-base', '--is-ancestor', ancestor, sha], { cwd, stdio: 'ignore' });
    return true;
  } catch {
    // Non-zero means "not an ancestor", but also "unknown object" — a commit that was rebased away
    // or never fetched. Both are correctly "not in this build".
    return false;
  }
}

function resolveSha(cwd: string, requested?: string): string | null {
  try {
    const target = requested && requested.trim() ? requested.trim() : 'HEAD';
    // stderr piped, not inherited: git's own "ambiguous argument" text is noise next to the
    // message below, and seeing both makes the CLI look like it failed twice.
    return execFileSync('git', ['rev-parse', target], {
      cwd,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    }).trim();
  } catch {
    return null;
  }
}

/**
 * `pointer status --deployed [sha]` — tell the server which comments this build carries live.
 *
 * Why the CLI owns this rather than the widget: pointer-init.md recommends shipping production
 * WITHOUT the widget, so the widget's beacon is absent exactly where "is it live yet?" matters
 * most. The CLI has the repository, so it can answer for any deployed sha, from CI or by hand.
 */
export async function deployedCommand(
  cwd: string,
  parsed: Record<string, string | boolean>,
): Promise<void> {
  const { server, token, project } = await getClient(cwd, parsed);

  const requested = typeof parsed['deployed'] === 'string' ? parsed['deployed'] : undefined;
  const sha = resolveSha(cwd, requested);
  if (!sha) {
    console.error(
      requested
        ? `${requested} is not a commit in this repository.`
        : 'Could not read HEAD — run this inside the deployed repository.',
    );
    process.exit(2);
  }

  // Applied but not yet deployed. Anything already deployed is skipped: DeployedAt is write-once
  // server-side, so re-sending it would be noise rather than a correction.
  let applied: any[] = [];
  try {
    const res = await api<any>(server, `/api/projects/${project}/comments?status=3&pageSize=200`, { token });
    applied = res?.items ?? res ?? [];
  } catch (err: any) {
    console.error(`Could not read applied comments: ${err?.message ?? err}`);
    process.exit(1);
  }

  const candidates = applied.filter((c) => c?.commitSha && !c?.deployedAt);
  const containedShas = candidates
    .filter((c) => contains(cwd, c.commitSha, sha))
    .map((c) => c.commitSha as string);

  // Reported even when nothing matched: the build itself is a fact worth recording, and a repeat
  // report is explicitly cheap (FirstSeen false, no ids).
  const unique = [...new Set(containedShas)];

  let result: any;
  try {
    result = await api<any>(server, `/api/projects/${project}/builds`, {
      method: 'POST',
      body: { sha, containsCommitShas: unique },
      token,
    });
  } catch (err: any) {
    console.error(`Could not report the build: ${err?.message ?? err}`);
    process.exit(1);
  }

  const marked = result?.deployedCommentIds?.length ?? 0;
  console.log(`${marked} comment${marked === 1 ? '' : 's'} marked deployed in ${sha.slice(0, 7)}`);
  process.exit(0);
}
