import { promises as fs } from 'node:fs';
import { dirname } from 'node:path';
import { api, ApiError } from './api.js';
import { resolveApiKey, tokenCacheFile, removeStaleRepoTokenCache, type ApiKeySource } from './credentials.js';

/**
 * Resolves the API key for `cwd`'s repo, honouring the full precedence in `resolveApiKey`
 * (env -> repo credentials.env -> global store). Pass `server` whenever it is already known so the
 * global-store step actually runs — see `credentials.ts` for why it is optional here.
 */
export async function readApiKey(cwd: string, server?: string): Promise<string | undefined> {
  const { key } = await resolveApiKey(cwd, server);
  return key;
}

/** Same as `readApiKey`, but also reports which of the three sources answered — for `whoami`/doctor. */
export async function readApiKeyWithSource(
  cwd: string,
  server?: string,
): Promise<{ key: string | undefined; source: ApiKeySource }> {
  return resolveApiKey(cwd, server);
}

/**
 * Exchanges an API key for a JWT, caching the result under the global per-(server,key) cache file
 * (see `tokenCacheFile`) so repeated commands in the same repo — or a different repo against the
 * same server and key — don't re-login every time.
 */
export async function resolveToken(
  server: string,
  cwd: string,
  explicitApiKey?: string,
): Promise<string | undefined> {
  // Best-effort cleanup of the old repo-local cache this replaces — see removeStaleRepoTokenCache.
  await removeStaleRepoTokenCache(cwd);

  const apiKey = explicitApiKey || (await readApiKey(cwd, server));
  if (!apiKey) return undefined;

  const cacheFile = tokenCacheFile(server, apiKey);
  try {
    const cached = JSON.parse(await fs.readFile(cacheFile, 'utf8'));
    const token = typeof cached?.token === 'string' ? cached.token.trim() : '';
    if (token) return token;
  } catch {
    // Missing, unreadable, or not JSON — fall through to a fresh login.
  }

  try {
    const login = await api<{ status?: string; token?: string }>(
      server,
      '/api/auth/login-with-key',
      {
        method: 'POST',
        body: { apiKey },
      },
    );

    if (login?.token) {
      try {
        await fs.mkdir(dirname(cacheFile), { recursive: true });
        await fs.writeFile(cacheFile, JSON.stringify({ token: login.token }), 'utf8');
      } catch {
        // A cache write failure must not fail the login itself.
      }
      return login.token;
    }
  } catch (err: any) {
    if (err instanceof ApiError && err.code === 401) {
      // invalid key
      return undefined;
    }
  }

  return undefined;
}
