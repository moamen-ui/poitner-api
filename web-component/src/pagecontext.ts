// Page-context capture: console errors/warnings + failed/slow network requests, buffered
// continuously in the browser while the widget is active. Only ever sent when a comment is
// submitted with the "Report as a bug" checkbox on — see element.ts createComment(). Buffering
// itself is gated by the project's PageContextCaptureEnabled toggle (checked once at widget init
// via GET /api/projects/{key}/capture-config); the checkbox only controls TRANSMISSION, since a
// console error that already happened can't be captured retroactively.
//
// Privacy: only metadata is ever recorded — no request/response headers or bodies, and network
// URLs have their query string stripped before being buffered (query params can carry tokens).

export interface ConsoleEntryInput {
  level: string;
  message: string;
  stack?: string;
  count: number;
  occurredAt: string;
}

export interface NetworkEntryInput {
  method: string;
  url: string;
  statusCode: number | null;
  durationMs: number;
  occurredAt: string;
}

export interface PageContextPayload {
  sessionId: string;
  consoleEntries: ConsoleEntryInput[];
  networkEntries: NetworkEntryInput[];
}

const MAX_ENTRIES = 20;
const MAX_AGE_MS = 30 * 60 * 1000; // 30 minutes
const SLOW_REQUEST_MS = 3000;

const consoleEntries: ConsoleEntryInput[] = [];
const networkEntries: NetworkEntryInput[] = [];

let started = false;
let recording = false; // reentrancy guard — recording itself must never trigger recording
let originalConsoleError: typeof console.error | null = null;
let originalConsoleWarn: typeof console.warn | null = null;
let originalFetch: typeof window.fetch | null = null;
let ownOrigins: string[] = [];

function now(): string {
  return new Date().toISOString();
}

// Drop entries older than MAX_AGE_MS, then cap to MAX_ENTRIES (oldest evicted first) — keeps a
// long-lived SPA tab from growing the buffer unbounded.
function trim(list: unknown[], maxAgeGetter: (item: unknown) => string): void {
  const cutoff = Date.now() - MAX_AGE_MS;
  while (list.length && new Date(maxAgeGetter(list[0])).getTime() < cutoff) list.shift();
  while (list.length > MAX_ENTRIES) list.shift();
}

function stringifyArg(arg: unknown): string {
  if (typeof arg === 'string') return arg;
  if (arg instanceof Error) return arg.message;
  try {
    return JSON.stringify(arg);
  } catch {
    return String(arg);
  }
}

function extractStack(args: unknown[]): string | undefined {
  const err = args.find((a) => a instanceof Error) as Error | undefined;
  return err?.stack?.slice(0, 4000);
}

function recordConsole(level: string, args: unknown[]): void {
  if (recording) return;
  recording = true;
  try {
    const message = args.map(stringifyArg).join(' ').slice(0, 2000);
    // Never record the widget's own diagnostic logs (avoids self-pollution/loops).
    if (message.startsWith('[pointer-feedback]')) return;
    const stack = extractStack(args);
    const last = consoleEntries[consoleEntries.length - 1];
    if (last && last.level === level && last.message === message) {
      last.count += 1;
      last.occurredAt = now();
    } else {
      consoleEntries.push({ level, message, stack, count: 1, occurredAt: now() });
    }
    trim(consoleEntries, (e) => (e as ConsoleEntryInput).occurredAt);
  } catch {
    /* never let capture break the page */
  } finally {
    recording = false;
  }
}

function stripQuery(url: string): string {
  const cut = url.search(/[?#]/);
  return cut >= 0 ? url.slice(0, cut) : url;
}

function isOwnRequest(url: string): boolean {
  try {
    const origin = new URL(url, window.location.href).origin;
    return ownOrigins.includes(origin);
  } catch {
    return false;
  }
}

function recordNetwork(method: string, url: string, statusCode: number | null, durationMs: number): void {
  try {
    networkEntries.push({ method, url: stripQuery(url), statusCode, durationMs, occurredAt: now() });
    trim(networkEntries, (e) => (e as NetworkEntryInput).occurredAt);
  } catch {
    /* never let capture break the page */
  }
}

/**
 * Begin buffering console errors/warnings and failed/slow fetch requests. Idempotent — safe to
 * call more than once. `server` (the widget's API origin) and the widget script's own origin are
 * excluded from network capture so the widget never records its own traffic.
 */
export function startPageContextCapture(server: string, scriptOrigin?: string): void {
  if (started) return;
  started = true;

  ownOrigins = [server, scriptOrigin, window.location.origin]
    .filter((o): o is string => !!o)
    .map((o) => {
      try { return new URL(o).origin; } catch { return o; }
    });

  originalConsoleError = console.error.bind(console);
  originalConsoleWarn = console.warn.bind(console);
  console.error = (...args: unknown[]) => {
    recordConsole('error', args);
    originalConsoleError!(...args);
  };
  console.warn = (...args: unknown[]) => {
    recordConsole('warn', args);
    originalConsoleWarn!(...args);
  };

  originalFetch = window.fetch.bind(window);
  window.fetch = (...args: Parameters<typeof fetch>) => {
    const url = typeof args[0] === 'string' ? args[0] : (args[0] as Request).url;
    if (isOwnRequest(url)) return originalFetch!(...args);

    const method = (args[1]?.method || (args[0] as Request)?.method || 'GET').toUpperCase();
    const start = Date.now();
    return originalFetch!(...args).then(
      (response) => {
        const durationMs = Date.now() - start;
        if (!response.ok || durationMs >= SLOW_REQUEST_MS) {
          recordNetwork(method, url, response.status, durationMs);
        }
        return response;
      },
      (err) => {
        recordNetwork(method, url, null, Date.now() - start);
        throw err;
      },
    );
  };
}

/** Restore original console/fetch (called on disconnectedCallback so a removed widget leaves no trace). */
export function stopPageContextCapture(): void {
  if (!started) return;
  if (originalConsoleError) console.error = originalConsoleError;
  if (originalConsoleWarn) console.warn = originalConsoleWarn;
  if (originalFetch) window.fetch = originalFetch;
  started = false;
}

function getOrCreateSessionId(): string {
  const KEY = 'pointer_page_session_id';
  try {
    let id = sessionStorage.getItem(KEY);
    if (!id) {
      id = typeof crypto !== 'undefined' && crypto.randomUUID
        ? crypto.randomUUID()
        : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
      sessionStorage.setItem(KEY, id);
    }
    return id;
  } catch {
    return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  }
}

/**
 * Current buffer snapshot for attaching to a "Report as a bug" comment. Returns null when capture
 * was never started (project toggle is off) or nothing has been buffered yet.
 */
export function getPageContextPayload(): PageContextPayload | null {
  if (!started) return null;
  if (consoleEntries.length === 0 && networkEntries.length === 0) return null;
  return {
    sessionId: getOrCreateSessionId(),
    consoleEntries: consoleEntries.slice(),
    networkEntries: networkEntries.slice(),
  };
}

export function isPageContextCaptureStarted(): boolean {
  return started;
}
