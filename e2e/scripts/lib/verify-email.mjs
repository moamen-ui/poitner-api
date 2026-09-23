// DB-14 (docs/db/execution/DB-14-email-verification-and-password-policy.md §3.2/§3.4): a member an
// admin creates via `POST /api/admin/users` starts UNVERIFIED (`EmailVerifiedAt == null`) and gets
// a best-effort verification mail (`EmailVerificationService.SendAsync`,
// Application/Services/Implementation/EmailVerificationService.cs:84 — link shape
// `{app}/verify-email?token=<urlencoded token>`). §3.4's global filter then answers every non-GET
// `/api/admin/*` call from that identity with 403 + header `X-Email-Verification-Required` until
// `POST /api/auth/verify-email` redeems that token (anonymous — the token is the credential).
//
// This closes that loop the realistic way: through Mailpit, the same inbox the compose stack
// already wires the API's SMTP output to (.github/workflows/e2e.yml "Create dev .env";
// `docker-compose.yaml` service `mailpit`) — so `seed.mjs` (and any spec that creates its own
// admin-tier persona mid-test, e.g. via `POST /api/admin/users` or an addressed tenant/workspace
// invite) can finish what `UserService.CreateAsync` started before driving that persona through an
// admin write.
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, post } from './api.mjs';
import { awaitMessage, extractLink } from './mail.mjs';

const here = dirname(fileURLToPath(import.meta.url));
// e2e/scripts/lib -> e2e/scripts -> e2e -> repo root (docker-compose.yaml lives there — same
// convention as e2e/scripts/upgrade-assert.mjs's psql()).
const repoRoot = resolve(here, '..', '..', '..');

function psql(sql) {
  // Bare `docker compose exec` — relies on COMPOSE_PROJECT_NAME/COMPOSE_FILE (native Docker
  // Compose env vars) when scripts/local-e2e-gate.sh has exported them to target its isolated
  // project instead of the shared dev stack; unset, this is exactly the call CI has always made.
  return execFileSync(
    'docker',
    ['compose', 'exec', '-T', 'db', 'psql', '-U', 'pointer', '-d', 'pointer', '-tAc', sql],
    { cwd: repoRoot, encoding: 'utf8' },
  ).trim();
}

function tokenFromVerifyLink(link) {
  const match = link.match(/[?&]token=([^&]+)/);
  if (!match) throw new Error(`verify-email link had no token param: ${link}`);
  return decodeURIComponent(match[1]);
}

/**
 * Verifies one persona's e-mail address so it clears the DB-14 gate on `/api/admin/*` non-GETs.
 *
 * Idempotent: a persona whose `GET /api/me` already reports `emailVerified: true` (grandfathered,
 * verified-at-creation per §3.2, or already verified by an earlier call) is left alone — no Mailpit
 * round trip, no extra call against the anonymous `signup`-rate-limited bucket that
 * `/api/auth/verify-email` shares with register/forgot-password/reset-password (5/h per IP in
 * production; the compose stack raises this to 1000/h for exactly this reason — see
 * `docker-compose.yaml` `Security__RateLimits__SignupPerHour` and
 * `API/Extensions/RateLimitingExtensions.cs`'s doc-comment on that override).
 *
 * Tolerant to Mailpit being absent (a local `just up` run without the `e2e` compose profile, say):
 * on any failure to find/parse the mail it logs a warning and returns unverified rather than
 * throwing, UNLESS `E2E_VERIFY_VIA_DB=1` is set, in which case it falls back to setting
 * `users.email_verified_at` directly. Never used as the default path — the point of this helper is
 * to prove the real mailed-link flow works, per the task's ask; the DB route is an escape hatch for
 * a local machine with no Mailpit.
 *
 * @param {string} email - the persona's address (normalised address the mail was sent to).
 * @param {string} token - that persona's own bearer token, used only to read `GET /api/auth/me` for
 *   the idempotency check.
 * @returns {Promise<{ verified: boolean, via: 'already'|'mailpit'|'db'|'skipped', error?: string }>}
 */
export async function verifyPersonaEmail(email, token) {
  const me = await get('/api/auth/me', { token }).catch(() => null);
  if (me?.emailVerified) return { verified: true, via: 'already' };

  try {
    const message = await awaitMessage({ to: email, subjectIncludes: 'Verify your', timeoutMs: 15000 });
    const link = extractLink(message.html, '/verify-email?token=');
    const verifyToken = tokenFromVerifyLink(link);
    await post('/api/auth/verify-email', { token: verifyToken });
    return { verified: true, via: 'mailpit' };
  } catch (err) {
    if (process.env.E2E_VERIFY_VIA_DB === '1') {
      console.warn(
        `verifyPersonaEmail: Mailpit path failed for ${email} (${err.message}); falling back to ` +
          'the direct DB route (E2E_VERIFY_VIA_DB=1).',
      );
      const normalized = email.trim().toLowerCase().replace(/'/g, "''");
      psql(
        `update users set email_verified_at = now() where lower(email) = '${normalized}' and email_verified_at is null;`,
      );
      return { verified: true, via: 'db' };
    }
    console.warn(
      `verifyPersonaEmail: Mailpit unreachable/failed for ${email} (${err.message}); leaving this ` +
        'persona unverified for the DB-14 gate. Set E2E_VERIFY_VIA_DB=1 to fall back to a direct ' +
        'DB update instead (e.g. for a local run with no Mailpit).',
    );
    return { verified: false, via: 'skipped', error: err.message };
  }
}
