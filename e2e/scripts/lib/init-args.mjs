// The non-interactive `pointer init` argv, in one place.
//
// R1-02-tests owns the exact flag spellings. Every other suite that runs init reproduces the same
// eight-flag incantation, so a rename there would otherwise mean hunting the literal through
// several spec files — and a missed one fails as "init exited 1" with no hint that the cause is a
// flag that no longer exists.
import { keys } from './state.mjs';

export const SERVER = process.env.POINTER_SERVER || 'http://localhost:8090';

/** The shared fixture project. Only for scenarios whose stack detection matches it — see below. */
export const DEFAULT_PROJECT = 'e2e-alpha';

/**
 * argv for a non-interactive init against an existing project.
 *
 * NOTE on the project: a project's `frontend`/`backend` stack is WRITE-ONCE-IF-EMPTY
 * (ProjectService — only `aiTools` grows afterwards). The first stack POST against a key wins for
 * the lifetime of the database, so a scenario whose fixture detects differently must pass its own
 * throwaway key rather than reuse the default, or it silently pins the shared project's stack and
 * breaks every later scenario that asserts on it.
 */
export function initArgs({ project = DEFAULT_PROJECT, server = SERVER, extra = [] } = {}) {
  return [
    'init',
    '--server', server,
    '--key', keys().developer.apiKey,
    '--project', project,
    '--environment', 'local',
    '--tool', 'other',
    '--yes',
    // NOT --json. The JSON envelope replaces init's human output entirely, including the
    // `✔ Design tokens: …` line R3-02-01 asserts on. A caller that wants the envelope adds
    // `--json` through `extra`; making it the default would silently delete that line from
    // every scenario's stdout.
    ...extra,
  ];
}

/** Convenience for the common case. */
export const INIT_ARGS = () => initArgs();

/**
 * Environment for a CLI run.
 *
 * Inherits the real environment so node/git resolve, and pins the server so a stray
 * POINTER_SERVER in the developer's shell cannot point a test at production.
 */
export const INIT_ENV = { ...process.env, POINTER_SERVER: SERVER };
