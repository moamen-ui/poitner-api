import { get, post, ApiError } from '../../scripts/lib/api.mjs';

export interface EnsuredProject {
  id: number;
  key: string;
}

/**
 * Idempotent "create or find" for a dedicated e2e project, extracted from the pattern
 * widget.spec.ts's beforeAll invented — R3-04-tests.md §Spec files names it as a shared helper.
 *
 * The 409-tolerant conflict path matters because Playwright can re-run a spec file (worker
 * restart, `--only` dispatch) against a database that already holds the project: a plain POST
 * would turn "already exists" into a red beforeAll and every scenario in the file would read
 * as broken.
 *
 * Only ever call this with a DEDICATED key (e2e-privacy, e2e-widget-smoke, …) — never
 * e2e-alpha (its rows are the AI ground truth in state/expected.json) or e2e-beta (R1-05's
 * origin-enforcement fixture).
 */
export async function ensureProject(
  wsAdminToken: string,
  key: string,
  name: string,
): Promise<EnsuredProject> {
  try {
    return await post('/api/admin/projects', { key, name }, { token: wsAdminToken });
  } catch (err) {
    if (!(err instanceof ApiError) || err.status !== 409) throw err;
    const all = await get('/api/admin/projects', { token: wsAdminToken });
    const found = (all as Array<{ id: number; key: string }>).find((p) => p.key === key);
    if (!found) {
      throw new Error(`${key} conflicted but wasn't found via list — key collision with another tenant?`);
    }
    return found;
  }
}
