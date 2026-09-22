// Thin fetch wrapper around the Result<T> envelope every pointer-api response uses.
// Node 18+ global fetch — no dependency needed.
export const BASE_URL = process.env.E2E_API_URL || 'http://localhost:8090';

export class ApiError extends Error {
  constructor(message, status, body) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

// Performs the request and returns the full outcome WITHOUT throwing. Use this whenever the
// status itself is the assertion — the origin, rate-limit and permission matrices are all of the
// form "this header gets 403, that one gets 200", and expressing those through thrown errors
// turns every row into a try/catch that is easy to write as a silently-passing no-op.
export async function raw(method, path, { token, body, headers: extraHeaders } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  // Caller-supplied headers win, so a scenario can set Origin/Referer — or deliberately send no
  // Origin at all, which is node fetch's default and is itself a case the gate treats specially.
  Object.assign(headers, extraHeaders ?? {});

  const url = path.startsWith('http://') || path.startsWith('https://') ? path : `${BASE_URL}${path}`;
  const res = await fetch(url, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await res.text();
  let json;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }

  return {
    status: res.status,
    ok: res.ok,
    headers: res.headers,
    body: json ?? text,
    text,
    // The Result<T> envelope's own success flag, which can be false on a 200.
    isSuccess: json && typeof json === 'object' ? json.isSuccess : undefined,
    data: json && typeof json === 'object' ? (json.data ?? json) : json,
  };
}

async function call(method, path, opts = {}) {
  const res = await raw(method, path, opts);

  if (!res.ok) {
    throw new ApiError(`${method} ${path} -> ${res.status}`, res.status, res.body);
  }
  if (res.isSuccess === false) {
    throw new ApiError(`${method} ${path} -> Result failure: ${res.body?.message}`, res.status, res.body);
  }
  return res.data;
}

export const get = (path, opts) => call('GET', path, opts);
export const post = (path, body, opts = {}) => call('POST', path, { ...opts, body });
export const patch = (path, body, opts = {}) => call('PATCH', path, { ...opts, body });
export const put = (path, body, opts = {}) => call('PUT', path, { ...opts, body });
export const del = (path, opts) => call('DELETE', path, opts);

export const getRaw = (path, opts) => raw('GET', path, opts);
export const postRaw = (path, body, opts = {}) => raw('POST', path, { ...opts, body });
export const patchRaw = (path, body, opts = {}) => raw('PATCH', path, { ...opts, body });
export const putRaw = (path, body, opts = {}) => raw('PUT', path, { ...opts, body });
export const delRaw = (path, opts) => raw('DELETE', path, opts);

// R5-59 put a per-e-mail fixed-window limit on POST /api/auth/login (10 requests / 15 min,
// normalised e-mail in the JSON body — see API/Extensions/RateLimitingExtensions.cs policy
// "password-login"). The suite logs the same seeded personas in dozens of times per run (every
// spec that needs an admin/tester/etc. token calls login() in its own beforeAll/test body), which
// blew straight through that budget and turned the whole run into a wall of 429s.
//
// Fix: cache the JWT per (baseUrl, email) for the life of this process. playwright.config.ts runs
// this suite with workers: 1 and fullyParallel: false, so a module-level Map is a real per-run
// cache, not a per-worker illusion — and each phase (`scripts/pw.sh <dir>`) is its own `npx
// playwright test` process, so the cache also resets cleanly between phases.
//
// A handful of specs deliberately test login itself (wrong password, passwordless-account
// refusal, the login-with-invite 429 bucket, …) — those must never see a cached success. They
// either exercise raw()/postRaw() directly (bypassing this helper entirely — see
// api/quick-access.spec.mjs's R2-05-05 and api/login-with-invite-429.spec.mjs's negative control)
// or can pass { forceFresh: true } here to force a real round trip and skip/refresh the cache.
const tokenCache = new Map();

export async function login(email, password, { forceFresh = false } = {}) {
  const cacheKey = `${BASE_URL}::${email}`;
  if (!forceFresh) {
    const cached = tokenCache.get(cacheKey);
    if (cached) return cached;
  }

  const promise = (async () => {
    const res = await raw('POST', '/api/auth/login', { body: { email, password } });
    if (res.status === 429) {
      throw new Error(
        `login rate-limited for ${email} (POST /api/auth/login -> 429) — use the cached token ` +
          'or forceFresh only where the test needs a real login',
      );
    }
    if (!res.ok) {
      throw new ApiError(`POST /api/auth/login -> ${res.status}`, res.status, res.body);
    }
    const data = res.data;
    if (data?.status !== 'ok' || !data.token) {
      throw new Error(`login failed for ${email}: status=${data?.status}`);
    }
    return { token: data.token, user: data.user };
  })();

  if (!forceFresh) {
    tokenCache.set(cacheKey, promise);
    // A failed login must not poison the cache for a later, legitimate retry.
    promise.catch(() => tokenCache.delete(cacheKey));
  }
  return promise;
}
