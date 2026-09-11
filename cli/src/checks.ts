export interface CheckResult {
  id: string;
  status: 'ok' | 'warn' | 'error';
  message: string;
}

export async function runInitChecks(server: string, projectKey: string, env: string, token?: string): Promise<CheckResult[]> {
    // Stub for now. Just return empty array since doctor is R1-04.
    return [];
}
