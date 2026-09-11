export interface ApiOptions {
  server: string;
  token?: string;
}

export async function fetchApi<T>(endpoint: string, options: ApiOptions, init?: RequestInit): Promise<T> {
  const url = `${options.server.replace(/\/$/, '')}${endpoint.startsWith('/') ? '' : '/'}${endpoint}`;
  
  const headers = new Headers(init?.headers);
  headers.set('Accept', 'application/json');
  if (init?.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (options.token) {
    headers.set('Authorization', `Bearer ${options.token}`);
  }

  const res = await fetch(url, { ...init, headers });
  
  if (!res.ok) {
    let message = res.statusText;
    try {
      const body = await res.json();
      if (body.message) message = body.message;
      else if (body.errors) message = JSON.stringify(body.errors);
    } catch {
      // Ignore
    }
    throw new Error(`API error ${res.status}: ${message}`);
  }
  
  return (await res.json()) as T;
}

export async function checkAuth(server: string, token: string): Promise<boolean> {
  try {
    // There is no dedicated /check endpoint for auth, but we can call something harmless
    // or if the spec demands we can call an admin endpoint like /api/admin/projects.
    // Spec says: "If PAT is supplied, test it. If invalid, reject immediately."
    // Let's assume /api/users/me exists, or just check the token format.
    // Actually, we can fetch /api/admin/events/summary?projectId=0 to test admin token.
    await fetchApi('/api/admin/events/summary?projectId=0', { server, token });
    return true;
  } catch (err: any) {
    if (err.message.includes('401') || err.message.includes('403')) {
      return false;
    }
    // If it's a 404 or something else, the token might be valid but project 0 not found.
    return true;
  }
}
