/**
 * Page-context capture: a rolling buffer of console errors/warnings, uncaught errors and
 * failed/slow network requests, attached to a comment when "Report as a bug" is checked.
 *
 * Buffering starts as soon as the project has `pageContextCaptureEnabled` (see element.ts) so the
 * report can include what happened *before* the stakeholder opened the widget. Nothing leaves the
 * browser until a bug report is submitted.
 *
 * What is recorded (and documented on the privacy/data pages — keep them in sync):
 * - `console.error` / `console.warn` calls, uncaught exceptions and unhandled promise rejections
 *   (message + stack, deduplicated by consecutive identical message).
 * - Network requests — both `fetch` and `XMLHttpRequest` (Angular HttpClient and axios use XHR by
 *   default) — but only the ones that failed (non-2xx/3xx, network error) or took ≥ 3s. Query
 *   strings are stripped. Successful requests are never recorded.
 * - The widget's own API traffic is excluded by construction: it goes through `rawFetch`, the
 *   un-patched fetch, so it never reaches the recorder. There is deliberately NO origin-based
 *   exclusion any more — the app under review may share its origin with the page or even with the
 *   Pointer server (the dashboards dogfood the widget against the same API), and excluding those
 *   silently produced empty reports.
 */
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
let originalXhrOpen: typeof XMLHttpRequest.prototype.open | null = null;
let originalXhrSend: typeof XMLHttpRequest.prototype.send | null = null;
let onWindowError: ((e: ErrorEvent) => void) | null = null;
let onUnhandledRejection: ((e: PromiseRejectionEvent) => void) | null = null;

function now(): string {
  return new Date().toISOString();
}

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
  pushConsole(level, args.map(stringifyArg).join(' '), extractStack(args));
}

function pushConsole(level: string, rawMessage: string, stack: string | undefined): void {
  if (recording) return;
  recording = true;
  try {
    const message = rawMessage.slice(0, 2000);
    if (message.startsWith('[pointer-feedback]')) return;
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

function shouldRecord(statusCode: number | null, durationMs: number): boolean {
  // null = network error / aborted; 0 is what XHR reports for the same thing.
  if (statusCode === null || statusCode === 0) return true;
  if (statusCode >= 400) return true;
  return durationMs >= SLOW_REQUEST_MS;
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
 * The un-patched `fetch`. Every request the widget makes for itself must go through this so that
 * it is never mistaken for the host app's traffic (see the module comment). Safe to call before
 * capture starts — it simply falls back to the live `window.fetch`.
 */
export function rawFetch(url: string, opts?: RequestInit): Promise<Response> {
  const f = originalFetch ?? window.fetch;
  return f.call(window, url, opts);
}

function patchFetch(): void {
  // Keep the unbound reference so `stop` can restore the exact original (identity-preserving).
  const original = window.fetch;
  originalFetch = original;
  window.fetch = (...args: Parameters<typeof fetch>) => {
    const url = typeof args[0] === 'string' ? args[0] : args[0] instanceof URL ? args[0].href : (args[0] as Request).url;
    const method = (args[1]?.method || (args[0] as Request)?.method || 'GET').toUpperCase();
    const start = Date.now();
    return original.apply(window, args).then(
      (response) => {
        const durationMs = Date.now() - start;
        if (shouldRecord(response.status, durationMs)) recordNetwork(method, url, response.status, durationMs);
        return response;
      },
      (err) => {
        recordNetwork(method, url, null, Date.now() - start);
        throw err;
      },
    );
  };
}

interface TrackedXhr extends XMLHttpRequest {
  __pfMethod?: string;
  __pfUrl?: string;
}

function patchXhr(): void {
  if (typeof XMLHttpRequest === 'undefined') return;
  const proto = XMLHttpRequest.prototype;
  originalXhrOpen = proto.open;
  originalXhrSend = proto.send;
  proto.open = function (this: TrackedXhr, method: string, url: string | URL, ...rest: unknown[]) {
    try {
      this.__pfMethod = String(method || 'GET').toUpperCase();
      this.__pfUrl = typeof url === 'string' ? url : String(url);
    } catch {
      /* never let capture break the page */
    }
    return (originalXhrOpen as Function).call(this, method, url, ...rest);
  } as typeof proto.open;
  proto.send = function (this: TrackedXhr, body?: Document | XMLHttpRequestBodyInit | null) {
    try {
      const start = Date.now();
      const method = this.__pfMethod || 'GET';
      const url = this.__pfUrl || '';
      this.addEventListener('loadend', () => {
        const durationMs = Date.now() - start;
        const status = this.status; // 0 = network error / aborted / CORS-blocked
        if (shouldRecord(status === 0 ? null : status, durationMs)) {
          recordNetwork(method, url, status === 0 ? null : status, durationMs);
        }
      });
    } catch {
      /* never let capture break the page */
    }
    return originalXhrSend!.call(this, body);
  };
}

function unpatchXhr(): void {
  if (typeof XMLHttpRequest === 'undefined') return;
  if (originalXhrOpen) XMLHttpRequest.prototype.open = originalXhrOpen;
  if (originalXhrSend) XMLHttpRequest.prototype.send = originalXhrSend;
  originalXhrOpen = null;
  originalXhrSend = null;
}

/**
 * Starts buffering. The `server` / `scriptOrigin` arguments are accepted for backward
 * compatibility with the previous origin-exclusion scheme and are no longer used — see the module
 * comment for why exclusion is now done by transport (`rawFetch`) rather than by origin.
 */
export function startPageContextCapture(_server?: string, _scriptOrigin?: string): void {
  if (started) return;
  started = true;

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

  // Uncaught exceptions and unhandled rejections do not necessarily pass through console.error
  // (frameworks with their own ErrorHandler may swallow them), so listen for them directly.
  onWindowError = (e: ErrorEvent) => {
    const err = e.error instanceof Error ? e.error : undefined;
    const where = e.filename ? ` (${e.filename}:${e.lineno}:${e.colno})` : '';
    pushConsole('error', `Uncaught ${e.message || (err && err.message) || 'error'}${where}`, err?.stack?.slice(0, 4000));
  };
  onUnhandledRejection = (e: PromiseRejectionEvent) => {
    const reason = e.reason;
    const err = reason instanceof Error ? reason : undefined;
    pushConsole('error', `Unhandled promise rejection: ${err ? err.message : stringifyArg(reason)}`, err?.stack?.slice(0, 4000));
  };
  window.addEventListener('error', onWindowError);
  window.addEventListener('unhandledrejection', onUnhandledRejection);

  patchFetch();
  patchXhr();
}

/** Restore original console/fetch/XHR (called on disconnectedCallback so a removed widget leaves no trace). */
export function stopPageContextCapture(): void {
  if (!started) return;
  if (originalConsoleError) console.error = originalConsoleError;
  if (originalConsoleWarn) console.warn = originalConsoleWarn;
  if (originalFetch) window.fetch = originalFetch;
  if (onWindowError) window.removeEventListener('error', onWindowError);
  if (onUnhandledRejection) window.removeEventListener('unhandledrejection', onUnhandledRejection);
  unpatchXhr();
  originalConsoleError = null;
  originalConsoleWarn = null;
  originalFetch = null;
  onWindowError = null;
  onUnhandledRejection = null;
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
 * The payload to attach to a bug report. Returns `null` only when capture never started (project
 * has it disabled). An **empty** payload is still a payload: "no console errors and no failed or
 * slow requests were seen on this page" is a real finding, and the server needs a snapshot to
 * exist for the dashboard/CLI to show that instead of nothing.
 */
export function getPageContextPayload(): PageContextPayload | null {
  if (!started) return null;
  return {
    sessionId: getOrCreateSessionId(),
    consoleEntries: consoleEntries.slice(),
    networkEntries: networkEntries.slice(),
  };
}

export function isPageContextCaptureStarted(): boolean {
  return started;
}

/** Test hook: clears buffered entries without touching the patches. */
export function resetPageContextBuffers(): void {
  consoleEntries.length = 0;
  networkEntries.length = 0;
}
