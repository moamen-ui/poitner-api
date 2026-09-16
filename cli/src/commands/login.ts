import { ask, closePrompts } from '../prompt.js';
import { findRepoRoot, readConfig } from '../config.js';
import { api } from '../api.js';
import { getBranding } from '../branding.js';
import { saveGlobalCredential } from '../credentials.js';
import { BUILD_DEFAULT_SERVER } from '../build-constants.js';

/**
 * Authenticates once per machine: exchanges an API key for the account it belongs to (the same
 * `/api/auth/login-with-key` + `/api/auth/me` pair `init` uses) and saves it to the global
 * credential store (see `credentials.ts`), keyed by server origin — every repo on this machine then
 * resolves a key for that server without asking again (see `resolveApiKey`'s precedence).
 */
export async function loginCommand(cwd: string, options: Record<string, string | boolean> = {}): Promise<void> {
  // A repo's own `.pointer/config.json` server is a convenience default only — login is global and
  // not scoped to "this repo" in any other way.
  const root = await findRepoRoot(cwd);
  const config = await readConfig(root).catch(() => ({}) as any);

  const server = (
    (typeof options['server'] === 'string' ? (options['server'] as string) : undefined) ||
    config.server ||
    process.env.POINTER_SERVER ||
    BUILD_DEFAULT_SERVER
  ).replace(/\/$/, '');

  const branding = await getBranding(server);
  const product = branding.productName;

  const flagKey = typeof options['key'] === 'string' ? (options['key'] as string) : undefined;
  let key = flagKey;
  let me: any;

  if (flagKey) {
    // A key handed on the command line is validated exactly once — there is no one to re-prompt
    // when it fails non-interactively, so a bad --key is a hard exit 3, same as `init --yes`.
    try {
      const login = await api<any>(server, '/api/auth/login-with-key', {
        method: 'POST',
        body: { apiKey: flagKey },
      });
      if (login?.status !== 'ok' || !login?.token) throw new Error(login?.status || 'invalid');
      me = login.user ?? (await api(server, '/api/auth/me', { token: login.token }));
    } catch {
      console.error('Invalid API key.');
      process.exit(3);
    }
  } else {
    let attempts = 0;
    while (!me) {
      key = await ask(`API key (from ${product} -> profile -> API key; input hidden)`, { secret: true });
      try {
        const login = await api<any>(server, '/api/auth/login-with-key', {
          method: 'POST',
          body: { apiKey: key },
        });
        if (login?.status !== 'ok' || !login?.token) throw new Error(login?.status || 'invalid');
        me = login.user ?? (await api(server, '/api/auth/me', { token: login.token }));
      } catch {
        attempts++;
        if (attempts >= 3) {
          console.error('Invalid API key.');
          process.exit(3);
        }
        console.error('Invalid API key. Try again.');
      }
    }
    closePrompts();
  }

  await saveGlobalCredential(server, { apiKey: key!, email: me?.email, displayName: me?.displayName });

  const who = me?.displayName ? `${me.displayName}${me?.email ? ` (${me.email})` : ''}` : me?.email ?? 'you';
  console.log(`✔ Signed in to ${server} as ${who} — saved for all repos on this machine`);
  process.exit(0);
}
