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
  /** True for workspace admins (Role.GrantsAdmin) — gates the popup's "+ Add project" affordance. */
  isAdmin?: boolean;
  /**
   * True for platform super admins, who own no tenant/workspace of their own. GET /api/admin/projects
   * returns a cross-tenant, platform-wide list for them (by design, for platform management) — so the
   * project picker must never be shown to them: it would list every tenant's projects side by side
   * (looking like duplicates) and activation always 404s regardless of which one is picked, since
   * ProjectService.EnsureAsync explicitly refuses to resolve any project by key for a super admin.
   */
  isSuperAdmin?: boolean;
  /**
   * True for quick-access (Client) accounts. They're barred from GET /api/admin/projects (a
   * browse/manage operation) so the popup must resolve their project by matching the current tab's
   * origin against their tenant's Project.AppUrl instead of listing — see 'projectForOrigin'.
   */
  isQuickAccess?: boolean;
}

/** A project the signed-in user can target, from GET /api/admin/projects. */
export interface ExtProject {
  key: string;
  name: string;
  isActive: boolean;
}

// popup/options -> background
export type BgRequest =
  | { type: 'getState' }
  | { type: 'getTabState'; tabId: number; hostname: string }
  | { type: 'login'; email: string; password: string; server: string }
  | { type: 'logout' }
  | { type: 'setServer'; server: string }
  | { type: 'setProjectForDomain'; hostname: string; project: string }
  | { type: 'listProjects' }
  | { type: 'projectForOrigin'; origin: string }
  | { type: 'createProject'; key: string; name: string; appUrl: string }
  | { type: 'activate'; tabId: number; hostname: string; origin: string; project: string; environment: string }
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
