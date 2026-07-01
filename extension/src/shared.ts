// Shared constants + message contracts used across background / content / popup.

export const DEFAULT_SERVER = 'https://api.pointer.moamen.work';

// storage.session holds the JWT (cleared when the browser closes / SW restarts
// keeps it for the session). storage.local holds non-secret prefs.
export const SESSION_KEYS = { token: 'token' } as const;
export const LOCAL_KEYS = {
  server: 'server',
  user: 'user',
  projectByDomain: 'projectByDomain', // { [hostname]: projectKey }
} as const;

// Placeholder token injected into the PAGE so the widget runs authenticated
// without ever seeing the real JWT — the background swaps in the real token on
// every proxied request. Must be truthy.
export const PROXY_TOKEN = '__pointer_via_proxy__';

export interface StoredUser {
  displayName?: string;
  email?: string;
  roleName?: string;
}

// popup/options -> background
export type BgRequest =
  | { type: 'getState' }
  | { type: 'getTabState'; tabId: number; hostname: string }
  | { type: 'login'; email: string; password: string; server: string }
  | { type: 'logout' }
  | { type: 'setServer'; server: string }
  | { type: 'setProjectForDomain'; hostname: string; project: string }
  | { type: 'activate'; tabId: number; hostname: string; project: string; environment: string }
  | { type: 'deactivate'; tabId: number };

// page (MAIN world, via content bridge) -> background: proxied API traffic
export type ProxyRequest =
  | { source: 'pointer-ext'; kind: 'fetch'; id: number; url: string; method: string; headers: Record<string, string>; body: string | null; auth: boolean }
  | { source: 'pointer-ext'; kind: 'upload'; id: number; url: string; base64: string; filename: string; contentType: string; project: string };

export interface ProxyResponse {
  ok: boolean;
  status: number;
  body: string;
  contentType: string | null;
}

export function hostnameOf(url: string): string {
  try { return new URL(url).hostname; } catch { return ''; }
}
