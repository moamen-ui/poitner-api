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

async function call(method, path, { token, body } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;

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

  if (!res.ok) {
    throw new ApiError(`${method} ${path} -> ${res.status}`, res.status, json ?? text);
  }
  if (json && json.isSuccess === false) {
    throw new ApiError(`${method} ${path} -> Result failure: ${json.message}`, res.status, json);
  }
  return json?.data ?? json;
}

export const get = (path, opts) => call('GET', path, opts);
export const post = (path, body, opts = {}) => call('POST', path, { ...opts, body });
export const patch = (path, body, opts = {}) => call('PATCH', path, { ...opts, body });
export const del = (path, opts) => call('DELETE', path, opts);

export async function login(email, password) {
  const data = await post('/api/auth/login', { email, password });
  if (data.status !== 'ok' || !data.token) {
    throw new Error(`login failed for ${email}: status=${data.status}`);
  }
  return { token: data.token, user: data.user };
}
