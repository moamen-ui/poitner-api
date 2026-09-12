import { api } from '../api.js';
import { postEvent } from '../events.js';
import { isStaged, commitAll, headSha, getRemoteUrl, commitUrlFor, getUserEmail } from './git.js';
import { fetchQueue } from './queue.js';
import { loadProjectContext } from './context.js';
import type { ApplyClientContext } from './types.js';

export type MarkAppliedOptions = {
  id: number | 'all';
  reply: string;
  noCommit?: boolean;
  dryRun?: boolean;
  tool?: string;
};

export type MarkAppliedResult = {
  committed: boolean;
  sha?: string;
  commitUrl?: string | null;
  patchedIds: number[];
};

export async function markApplied(
  options: MarkAppliedOptions,
  ctx: ApplyClientContext,
): Promise<MarkAppliedResult> {
  // Precondition: the AI must have staged its changes into git index
  if (!options.noCommit && !isStaged(ctx.cwd)) {
    if (options.id === 'all') {
      console.error('Nothing staged');
    } else {
      console.error(`Nothing staged for #${options.id}`);
    }
    process.exit(1);
  }

  const projectCtx = await loadProjectContext(ctx);
  const email = getUserEmail(ctx.cwd);
  const appliedByLabel = options.tool ? `${email} via ${options.tool}` : email;

  if (options.dryRun) {
    console.log(`[dry-run] Would mark comment ${options.id} as applied`);
    console.log(`[dry-run] Reply: ${options.reply}`);
    console.log(`[dry-run] AppliedBy: ${appliedByLabel}`);
    return {
      committed: false,
      patchedIds: typeof options.id === 'number' ? [options.id] : [],
    };
  }

  let commitUrl: string | null = null;
  let sha: string | undefined;
  const patchedIds: number[] = [];

  if (options.id === 'all') {
    // Single commit style: fetch all pending comments (status = 2)
    const pending = await fetchQueue(ctx, { status: 2 });
    const count = pending.length;
    const commitMsg = `Apply ${count} pending ${projectCtx.productName} comments`;

    if (!options.noCommit) {
      const commitRes = commitAll(commitMsg, ctx.cwd);
      if (!commitRes.success) {
        console.error(`Commit failed: ${commitRes.error}`);
        process.exit(1);
      }
      sha = headSha(ctx.cwd);
      const remote = getRemoteUrl(ctx.cwd);
      commitUrl = commitUrlFor(sha, remote);
    }

    for (const item of pending) {
      await api(ctx.server, `/api/comments/${item.id}`, {
        method: 'PATCH',
        body: {
          status: 3,
          reply: options.reply,
          appliedByLabel,
          commitUrl,
        },
        token: ctx.token,
      });
      patchedIds.push(item.id);
      await postEvent(ctx.server, ctx.token, {
        type: 'first_apply',
        projectKey: ctx.project,
        meta: { commentId: item.id },
      });
    }
  } else {
    // Separate commit style: commit single comment
    const id = options.id;
    let commentBody = '';
    try {
      const commentRes = await api<any>(ctx.server, `/api/comments/${id}`, {
        token: ctx.token,
      });
      commentBody = commentRes?.body ?? '';
    } catch {
      // If fetching comment details fails, use empty string
    }

    const shortBody = commentBody.slice(0, 60);
    const commitMsg = `Apply ${projectCtx.productName} comment #${id} — ${shortBody}`;

    if (!options.noCommit) {
      const commitRes = commitAll(commitMsg, ctx.cwd);
      if (!commitRes.success) {
        console.error(`Commit failed: ${commitRes.error}`);
        process.exit(1);
      }
      sha = headSha(ctx.cwd);
      const remote = getRemoteUrl(ctx.cwd);
      commitUrl = commitUrlFor(sha, remote);
    }

    await api(ctx.server, `/api/comments/${id}`, {
      method: 'PATCH',
      body: {
        status: 3,
        reply: options.reply,
        appliedByLabel,
        commitUrl,
      },
      token: ctx.token,
    });
    patchedIds.push(id);
    await postEvent(ctx.server, ctx.token, {
      type: 'first_apply',
      projectKey: ctx.project,
      meta: { commentId: id },
    });
  }

  return {
    committed: !options.noCommit,
    sha,
    commitUrl,
    patchedIds,
  };
}

export async function markFailed(
  id: number,
  reason: string,
  ctx: ApplyClientContext,
): Promise<void> {
  const replyBody = `Could not apply: ${reason}`;
  await api(ctx.server, `/api/comments/${id}/replies`, {
    method: 'POST',
    body: { body: replyBody },
    token: ctx.token,
  });

  await postEvent(ctx.server, ctx.token, {
    type: 'apply_failed',
    projectKey: ctx.project,
    meta: { commentId: id, reason },
  });
}
