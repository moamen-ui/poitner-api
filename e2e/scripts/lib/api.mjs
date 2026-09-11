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

  const res = await fetch(`${BASE_URL}${path}`, {
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

export async function login(email, password) {
  const data = await post('/api/auth/login', { email, password });
  if (data.status !== 'ok' || !data.token) {
    throw new Error(`login failed for ${email}: status=${data.status}`);
  }
  return { token: data.token, user: data.user };
}
