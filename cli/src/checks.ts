import { fetchApi } from './api.js';

export interface CheckResult {
  name: string;
  status: 'pass' | 'fail' | 'warn';
  message: string;
}

export async function runInitChecks(server: string, projectKey: string, env: string, token?: string): Promise<CheckResult[]> {
  const results: CheckResult[] = [];
  
  // 1. API Connection
  try {
    // A simple endpoint to test connection, could just hit /check page or a public api.
    const url = `${server.replace(/\/$/, '')}/check?project=${encodeURIComponent(projectKey)}&environment=${encodeURIComponent(env)}`;
    const res = await fetch(url);
    if (res.ok) {
      results.push({ name: 'API Connection', status: 'pass', message: `Connected to ${server}` });
    } else {
      results.push({ name: 'API Connection', status: 'fail', message: `HTTP ${res.status}` });
    }
  } catch (err: any) {
    results.push({ name: 'API Connection', status: 'fail', message: err.message });
  }

  // 2. Auth token (if provided)
  if (token) {
    try {
      // Test the token
      await fetchApi('/api/admin/events/summary?projectId=0', { server, token });
      results.push({ name: 'Authentication', status: 'pass', message: 'Valid token' });
    } catch (err: any) {
      if (err.message.includes('401') || err.message.includes('403')) {
        results.push({ name: 'Authentication', status: 'fail', message: 'Invalid or expired token' });
      } else {
        // Assume valid but 404 or something else
        results.push({ name: 'Authentication', status: 'pass', message: 'Valid token (could not verify exact project permissions)' });
      }
    }
  }

  return results;
}
