import { spawn } from 'node:child_process';
import { hostname } from 'node:os';
import { api } from './api.js';

/** Shape returned by the server on POST /api/auth/device/start. */
interface DeviceStartResponse {
  deviceCode: string;
  userCode: string;
  verificationUrl: string;
  expiresInSeconds: number;
  intervalSeconds: number;
}

/** Shape returned by the server on POST /api/auth/device/poll. */
interface DevicePollResponse {
  status: 'pending' | 'approved' | 'denied' | 'expired' | 'unknown';
  apiKey?: string;
  displayName?: string;
  email?: string;
  server?: string;
}

export interface DeviceLoginSuccess {
  apiKey: string;
  displayName?: string;
  email?: string;
}

export type DeviceLoginOutcome =
  | { ok: true; result: DeviceLoginSuccess }
  | { ok: false; reason: 'denied' | 'expired' };

/**
 * Best-effort browser open — macOS `open`, Windows `cmd /c start ""`, everything else `xdg-open`.
 * Detached and stdio-ignored so it never blocks or leaks output into the CLI's own stream, and any
 * failure (no display, sandboxed shell, missing binary) is swallowed: the URL and code are already
 * printed, so a browser that didn't open is an inconvenience, not a failure.
 */
function openBrowser(url: string): void {
  try {
    let child;
    if (process.platform === 'darwin') {
      child = spawn('open', [url], { detached: true, stdio: 'ignore' });
    } else if (process.platform === 'win32') {
      // The empty-title `""` argument is required — without it `start` treats the URL itself as
      // the window title when it's quoted.
      child = spawn('cmd', ['/c', 'start', '""', url], { detached: true, stdio: 'ignore', windowsHide: true });
    } else {
      child = spawn('xdg-open', [url], { detached: true, stdio: 'ignore' });
    }
    child.unref();
    child.on('error', () => {
      /* best-effort */
    });
  } catch {
    // best-effort
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Runs the full device-code ("sign in in your browser") flow against `server`, mirroring
 * `gh auth login`: mints a device/user code pair, prints the code and opens the browser (unless
 * `noBrowser`), then polls every `intervalSeconds` until the dashboard approves, denies, or the
 * code expires.
 *
 * Shared by `pointer login` and `pointer init`'s first-run "sign in in your browser" choice.
 */
export async function runDeviceLogin(
  server: string,
  options: { clientName?: string; noBrowser?: boolean } = {},
): Promise<DeviceLoginOutcome> {
  const clientName = options.clientName ?? `pointer-feedback CLI on ${hostname()}`;

  const start = await api<DeviceStartResponse>(server, '/api/auth/device/start', {
    method: 'POST',
    body: { clientName },
  });

  console.log('Open this link and enter the code to sign in:');
  console.log(`  ${start.verificationUrl}`);
  console.log(`  Code: ${start.userCode}`);
  console.log('Waiting for approval… (Ctrl+C to cancel)');

  if (!options.noBrowser) openBrowser(start.verificationUrl);

  const deadline = Date.now() + start.expiresInSeconds * 1000;
  const intervalMs = Math.max(1, start.intervalSeconds) * 1000;

  while (Date.now() < deadline) {
    await sleep(intervalMs);

    const poll = await api<DevicePollResponse>(server, '/api/auth/device/poll', {
      method: 'POST',
      body: { deviceCode: start.deviceCode },
    });

    if (poll.status === 'approved') {
      if (!poll.apiKey) {
        // Should be unreachable — the server only reports "approved" alongside the key. Treat as
        // expired (a retryable "start over") rather than trusting a key-less success.
        return { ok: false, reason: 'expired' };
      }
      return { ok: true, result: { apiKey: poll.apiKey, displayName: poll.displayName, email: poll.email } };
    }
    if (poll.status === 'denied') return { ok: false, reason: 'denied' };
    if (poll.status === 'expired' || poll.status === 'unknown') return { ok: false, reason: 'expired' };
    // 'pending' — keep polling.
  }

  return { ok: false, reason: 'expired' };
}
