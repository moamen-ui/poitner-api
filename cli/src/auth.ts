import { promises as fs } from 'node:fs';
import { join } from 'node:path';
import { api, ApiError } from './api.js';

export async function readApiKey(cwd: string): Promise<string | undefined> {
  if (process.env.POINTER_API_KEY) {
    return process.env.POINTER_API_KEY.trim();
  }
  try {
    const raw = await fs.readFile(join(cwd, '.pointer/credentials.env'), 'utf8');
    const match = raw.match(/^POINTER_API_KEY=(.*)$/m);
    return match?.[1]?.trim() || undefined;
  } catch {
    return undefined;
  }
}

export async function resolveToken(
  server: string,
  cwd: string,
  explicitApiKey?: string,
): Promise<string | undefined> {
  const tokenCacheFile = join(cwd, '.pointer/.token_cache');

  if (!explicitApiKey) {
    try {
      const cached = await fs.readFile(tokenCacheFile, 'utf8');
      const token = cached.trim();
      if (token) return token;
    } catch {}
  }

  const apiKey = explicitApiKey || (await readApiKey(cwd));
  if (!apiKey) return undefined;

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
        await fs.mkdir(join(cwd, '.pointer'), { recursive: true });
        await fs.writeFile(tokenCacheFile, login.token, 'utf8');
      } catch {}
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
