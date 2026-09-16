import { ask, closePrompts } from '../prompt.js';
import { findRepoRoot, readConfig, writeCredentials } from '../config.js';
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

  const scope = typeof options['scope'] === 'string' ? String(options['scope']).toLowerCase() : 'global';
  if (scope !== 'global' && scope !== 'repo') {
    console.error(`Invalid --scope "${options['scope']}". Valid values: global, repo.`);
    process.exit(2);
  }
  const who = me?.displayName ? `${me.displayName}${me?.email ? ` (${me.email})` : ''}` : me?.email ?? 'you';
  if (scope === 'repo') {
    // Repo scope: the multi-account case (a second identity on the same server for one repo). The
    // repo file wins over the global store in resolveApiKey, so this overrides a machine-wide key.
    await writeCredentials(root, key!, { server });
    console.log(`✔ Signed in to ${server} as ${who} — saved to .pointer/credentials.env (this repo only; overrides the global store here)`);
    process.exit(0);
  }
  await saveGlobalCredential(server, { apiKey: key!, email: me?.email, displayName: me?.displayName });
  console.log(`✔ Signed in to ${server} as ${who} — saved for all repos on this machine`);
  process.exit(0);
}
